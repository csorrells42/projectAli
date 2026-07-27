using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AvatarBuilder.Modules.Audio.Microphone;

/// <summary>
/// Microphone acquisition only. The capture callback copies the driver's
/// reusable buffer once into an immutable float array and publishes without
/// invoking or waiting for subscribers.
/// </summary>
public sealed class MicrophoneModule :
	IAudioModule,
	IModuleOutputSource<MicrophoneOutput>,
	IMicrophoneInputService,
	IDisposable
{
	private const int AudioClientDeviceInUse = unchecked((int)0x8889000A);

	private readonly ModuleOutputBroadcaster<MicrophoneOutput> _output = new();
	private readonly FrameModuleTiming _timing = new();
	private readonly int _deviceNumber;
	private readonly int _sampleRate;
	private readonly object _lifecycleGate = new();
	private IWaveIn? _capture;
	private MMDevice? _captureDevice;
	private WaveFormat? _captureFormat;
	private string _selectedInputId = "";
	private string _inputStatus = "Microphone stopped";
	private double _latestInputLevel;
	private long _latestInputLevelTimestamp;
	private long _rateAccumulator;
	private double _sampleBucket;
	private int _sampleBucketCount;
	private long _sequence;
	private int _started;
	private int _disposed;

	public MicrophoneModule(int deviceNumber = 0, int sampleRate = 16000)
	{
		_deviceNumber = deviceNumber;
		_sampleRate = sampleRate;
	}

	public IModuleOutputSubscription<MicrophoneOutput> Subscribe() =>
		_output.Subscribe();

	public string SelectedInputId =>
		Volatile.Read(ref _selectedInputId);

	public string InputStatus => Volatile.Read(ref _inputStatus);

	public IReadOnlyList<MicrophoneInputInfo> GetAvailableInputs()
	{
		using var enumerator = new MMDeviceEnumerator();
		string defaultId = "";
		try
		{
			using MMDevice defaultDevice = enumerator.GetDefaultAudioEndpoint(
				DataFlow.Capture,
				Role.Multimedia);
			defaultId = defaultDevice.ID;
		}
		catch
		{
		}
		MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(
			DataFlow.Capture,
			DeviceState.Active);
		var results = new List<MicrophoneInputInfo>(devices.Count);
		for (int index = 0; index < devices.Count; index++)
		{
			using MMDevice device = devices[index];
			results.Add(new(
				device.ID,
				device.FriendlyName,
				string.Equals(
					device.ID,
					defaultId,
					StringComparison.OrdinalIgnoreCase)));
		}
		return results;
	}

	public double GetInputLevel()
	{
		long timestamp = Volatile.Read(ref _latestInputLevelTimestamp);
		if (timestamp == 0
			|| Stopwatch.GetElapsedTime(timestamp) > TimeSpan.FromMilliseconds(500))
		{
			return 0d;
		}
		return Math.Clamp(Volatile.Read(ref _latestInputLevel), 0d, 1d);
	}

	public void SelectInput(string inputId)
	{
		ObjectDisposedException.ThrowIf(
			Volatile.Read(ref _disposed) != 0,
			this);
		ArgumentException.ThrowIfNullOrWhiteSpace(inputId);
		lock (_lifecycleGate)
		{
			string previousInputId = Volatile.Read(ref _selectedInputId);
			bool wasStarted = Volatile.Read(ref _started) != 0;
			StopCaptureLocked();
			Volatile.Write(ref _selectedInputId, inputId.Trim());
			Volatile.Write(ref _started, 1);
			try
			{
				StartCaptureWithDeviceReleaseRetryLocked();
			}
			catch (Exception exception)
			{
				bool restored = false;
				if (wasStarted && !string.IsNullOrWhiteSpace(previousInputId))
				{
					Volatile.Write(ref _selectedInputId, previousInputId);
					try
					{
						StartCaptureWithDeviceReleaseRetryLocked();
						restored = true;
					}
					catch
					{
					}
				}
				Volatile.Write(ref _started, restored ? 1 : 0);
				Volatile.Write(
					ref _inputStatus,
					restored
						? "Microphone switch failed; previous input restored"
						: "Microphone unavailable: " + exception.Message);
				throw;
			}
		}
	}

	public void Start()
	{
		ObjectDisposedException.ThrowIf(
			Volatile.Read(ref _disposed) != 0,
			this);
		if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
		{
			return;
		}
		lock (_lifecycleGate)
		{
			if (Volatile.Read(ref _disposed) != 0)
			{
				Volatile.Write(ref _started, 0);
				throw new ObjectDisposedException(nameof(MicrophoneModule));
			}
			try
			{
				StartCaptureWithDeviceReleaseRetryLocked();
			}
			catch (Exception exception)
			{
				StopCaptureLocked();
				Volatile.Write(ref _started, 0);
				Volatile.Write(
					ref _inputStatus,
					"Microphone unavailable: " + exception.Message);
				throw;
			}
		}
	}

	public TimeSpan GetIdleTime() => _timing.TimeWaited;
	public TimeSpan GetWorkingTime() => _timing.TimeWorked;

	private void DataAvailable(object? sender, WaveInEventArgs args)
	{
		WaveFormat? format = _captureFormat;
		if (!_output.CanAcceptAny
			|| format is null
			|| args.BytesRecorded < format.BlockAlign)
		{
			return;
		}
		_timing.WorkStarted(Stopwatch.GetTimestamp());
		float[] samples = ConvertToMono16Khz(
			args.Buffer,
			args.BytesRecorded,
			format);
		if (samples.Length == 0)
		{
			return;
		}
		double peak = 0d;
		foreach (float sample in samples)
		{
			peak = Math.Max(peak, Math.Abs(sample));
		}
		Volatile.Write(ref _latestInputLevel, peak);
		Volatile.Write(
			ref _latestInputLevelTimestamp,
			Stopwatch.GetTimestamp());
		_output.Publish(new MicrophoneOutput(
			Interlocked.Increment(ref _sequence),
			Stopwatch.GetTimestamp(),
			DateTime.UtcNow,
			_sampleRate,
			1,
			samples));
		_timing.FrameMovedOut(Stopwatch.GetTimestamp());
	}

	private float[] ConvertToMono16Khz(
		byte[] buffer,
		int bytesRecorded,
		WaveFormat format)
	{
		int frameCount = bytesRecorded / format.BlockAlign;
		if (frameCount <= 0 || format.SampleRate <= 0)
		{
			return [];
		}
		long outputCountLong = (
			_rateAccumulator + (long)frameCount * _sampleRate)
			/ format.SampleRate;
		if (outputCountLong <= 0)
		{
			AccumulateWithoutOutput(
				buffer,
				frameCount,
				format);
			return [];
		}
		var output = new float[checked((int)outputCountLong)];
		int outputIndex = 0;
		for (int frame = 0; frame < frameCount; frame++)
		{
			float mono = ReadMonoSample(
				buffer,
				frame * format.BlockAlign,
				format);
			_sampleBucket += mono;
			_sampleBucketCount++;
			_rateAccumulator += _sampleRate;
			while (_rateAccumulator >= format.SampleRate)
			{
				output[outputIndex++] = _sampleBucketCount == 0
					? mono
					: (float)(_sampleBucket / _sampleBucketCount);
				_rateAccumulator -= format.SampleRate;
				_sampleBucket = 0d;
				_sampleBucketCount = 0;
			}
		}
		if (outputIndex != output.Length)
		{
			Array.Resize(ref output, outputIndex);
		}
		return output;
	}

	private void AccumulateWithoutOutput(
		byte[] buffer,
		int frameCount,
		WaveFormat format)
	{
		for (int frame = 0; frame < frameCount; frame++)
		{
			_sampleBucket += ReadMonoSample(
				buffer,
				frame * format.BlockAlign,
				format);
			_sampleBucketCount++;
			_rateAccumulator += _sampleRate;
		}
	}

	private static float ReadMonoSample(
		byte[] buffer,
		int frameOffset,
		WaveFormat format)
	{
		int channels = Math.Max(1, format.Channels);
		int bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
		double sum = 0d;
		for (int channel = 0; channel < channels; channel++)
		{
			int offset = frameOffset + channel * bytesPerSample;
			sum += ReadSample(buffer, offset, format);
		}
		return (float)Math.Clamp(sum / channels, -1d, 1d);
	}

	private static float ReadSample(
		byte[] buffer,
		int offset,
		WaveFormat format)
	{
		bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
			|| format is WaveFormatExtensible extensible
				&& extensible.SubFormat == new Guid(
					"00000003-0000-0010-8000-00aa00389b71");
		if (isFloat && format.BitsPerSample == 32)
		{
			return BitConverter.Int32BitsToSingle(
				buffer[offset]
				| buffer[offset + 1] << 8
				| buffer[offset + 2] << 16
				| buffer[offset + 3] << 24);
		}
		return format.BitsPerSample switch
		{
			16 => (short)(buffer[offset] | buffer[offset + 1] << 8)
				/ 32768f,
			24 => ReadInt24(buffer, offset) / 8388608f,
			32 => (buffer[offset]
				| buffer[offset + 1] << 8
				| buffer[offset + 2] << 16
				| buffer[offset + 3] << 24) / 2147483648f,
			_ => 0f
		};
	}

	private static int ReadInt24(byte[] buffer, int offset)
	{
		int value = buffer[offset]
			| buffer[offset + 1] << 8
			| buffer[offset + 2] << 16;
		return (value & 0x00800000) == 0
			? value
			: value | unchecked((int)0xff000000);
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}
		lock (_lifecycleGate)
		{
			StopCaptureLocked();
		}
		Volatile.Write(ref _inputStatus, "Microphone stopped");
		_output.Dispose();
	}

	private void StartCaptureLocked()
	{
		IWaveIn? capture = null;
		MMDevice? device = null;
		try
		{
			using var enumerator = new MMDeviceEnumerator();
			string selectedId = Volatile.Read(ref _selectedInputId);
			if (!string.IsNullOrWhiteSpace(selectedId))
			{
				device = enumerator.GetDevice(selectedId);
			}
			else if (_deviceNumber == 0)
			{
				device = enumerator.GetDefaultAudioEndpoint(
					DataFlow.Capture,
					Role.Multimedia);
			}
			else
			{
				MMDeviceCollection devices =
					enumerator.EnumerateAudioEndPoints(
						DataFlow.Capture,
						DeviceState.Active);
				if (_deviceNumber < 0 || _deviceNumber >= devices.Count)
				{
					throw new InvalidOperationException(
						$"Microphone device {_deviceNumber} is not available.");
				}
				device = devices[_deviceNumber];
			}
			var wasapi = new WasapiCapture(
				device,
				useEventSync: true,
				audioBufferMillisecondsLength: 20)
			{
				ShareMode = AudioClientShareMode.Shared
			};
			capture = wasapi;
			_captureFormat = wasapi.WaveFormat;
			_rateAccumulator = 0;
			_sampleBucket = 0d;
			_sampleBucketCount = 0;
			capture.DataAvailable += DataAvailable;
			capture.StartRecording();
			_captureDevice = device;
			_capture = capture;
			Volatile.Write(ref _selectedInputId, device.ID);
			Volatile.Write(
				ref _inputStatus,
				"Listening to " + device.FriendlyName);
		}
		catch
		{
			if (capture is not null)
			{
				capture.DataAvailable -= DataAvailable;
				capture.Dispose();
			}
			device?.Dispose();
			_captureFormat = null;
			throw;
		}
	}

	private void StartCaptureWithDeviceReleaseRetryLocked()
	{
		for (int attempt = 0; ; attempt++)
		{
			try
			{
				StartCaptureLocked();
				return;
			}
			catch (COMException exception)
				when (exception.HResult == AudioClientDeviceInUse
					&& attempt < 3)
			{
				Thread.Sleep(25 * (attempt + 1));
			}
		}
	}

	private void StopCaptureLocked()
	{
		IWaveIn? capture = Interlocked.Exchange(ref _capture, null);
		MMDevice? device = Interlocked.Exchange(
			ref _captureDevice,
			null);
		_captureFormat = null;
		Volatile.Write(ref _latestInputLevel, 0d);
		Volatile.Write(ref _latestInputLevelTimestamp, 0L);
		if (capture is not null)
		{
			capture.DataAvailable -= DataAvailable;
			try
			{
				capture.StopRecording();
			}
			catch
			{
			}
			capture.Dispose();
		}
		device?.Dispose();
	}
}
