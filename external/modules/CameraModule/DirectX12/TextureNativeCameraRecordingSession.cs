using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.MediaFoundation;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed class TextureNativeCameraRecordingSession : IDisposable
{
	private static readonly TimeSpan RecordingStopTimeout = TimeSpan.FromSeconds(3L);

	private readonly object _stateLock = new object();

	private readonly object _processedDenoiseLock = new object();

	private readonly MediaFoundationCameraDeviceFactory.MediaFoundationScope _mediaFoundationScope;

	private readonly ITextureNativeDeviceManager _deviceManager;

	private readonly object _mediaSource;

	private readonly IMFSourceReader _reader;

	private readonly MediaFoundationTextureVideoRecorder? _recorder;

	private readonly MediaFoundationVideoRecorder? _processedRecorder;

	private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

	private readonly Task _recordingTask;

	private readonly int _width;

	private readonly int _height;

	private readonly double _fps;

	private readonly Guid _subtype;

	private readonly long _sampleDuration;

	private readonly string _recordingPipeline;

	private readonly bool _recordingDenoiseApplied;

	private readonly bool _recordingMatchesPreviewDenoise;

	private readonly double _recordingDenoiseStrength;

	private readonly bool _recordingColorPolishApplied;

	private readonly bool _recordingMatchesPreviewColor;

	private readonly VideoFrameColorSettings _recordingColorSettings;

	private byte[]? _previousProcessedDenoiseFrame;

	private bool _isPaused;

	private bool _isStopping;

	private long _nextSampleTime;

	private string _status = "Texture-native GPU recording started.";

	public string Path { get; }

	public bool IsPaused
	{
		get
		{
			lock (_stateLock)
			{
				return _isPaused;
			}
		}
	}

	public int SamplesWritten => _recorder?.SamplesWritten ?? _processedRecorder?.SamplesWritten ?? 0;

	internal TextureNativeCameraRecordingSession(string cameraName, CameraVideoMode? mode, string path, TextureNativeRecordingOptions? options = null)
	{
		Path = path;
		_mediaFoundationScope = MediaFoundationCameraDeviceFactory.Startup();
		(ITextureNativeDeviceManager, IMFSourceReader, object) tuple = TextureNativeCameraRecorder.OpenTextureSourceReader(cameraName, mode);
		_deviceManager = tuple.Item1;
		_reader = tuple.Item2;
		_mediaSource = tuple.Item3;
		(int, int, double, Guid) tuple2 = ReadCurrentFormat(_reader, mode);
		_width = tuple2.Item1;
		_height = tuple2.Item2;
		_fps = tuple2.Item3;
		_subtype = tuple2.Item4;
		_sampleDuration = Math.Max(1L, (long)Math.Round(10000000.0 / Math.Clamp(_fps, 1.0, 120.0)));
		if (options is not null && options.ProcessedOutputEnabled)
		{
			_processedRecorder = new MediaFoundationVideoRecorder(path, _width, _height, _fps, null);
			_recordingPipeline = "Texture-native processed BGRA bridge";
			_recordingDenoiseApplied = options.DenoiseEnabled;
			_recordingMatchesPreviewDenoise = true;
			_recordingDenoiseStrength = options.DenoiseStrength;
			_recordingColorSettings = options.ColorSettings;
			_recordingColorPolishApplied = options.ColorSettings.HasVisibleAdjustments;
			_recordingMatchesPreviewColor = true;
		}
		else
		{
			_recorder = new MediaFoundationTextureVideoRecorder(path, _width, _height, _fps, _subtype, _deviceManager.Manager);
			_recordingPipeline = "Media Foundation texture-native raw camera samples";
			_recordingDenoiseApplied = false;
			_recordingMatchesPreviewDenoise = options is null || !options.DenoiseEnabled;
			_recordingDenoiseStrength = options?.DenoiseStrength ?? 2.0;
			_recordingColorSettings = VideoFrameColorSettings.Off;
			_recordingColorPolishApplied = false;
			_recordingMatchesPreviewColor = options is null || !options.ColorSettings.HasVisibleAdjustments;
		}
		_recordingTask = Task.Run(delegate
		{
			CaptureLoop(_cancellation.Token);
		});
	}

	public void Pause()
	{
		lock (_stateLock)
		{
			_isPaused = true;
			_processedRecorder?.Pause();
			_status = "Texture-native GPU recording paused.";
		}
	}

	public void Resume()
	{
		lock (_stateLock)
		{
			_isPaused = false;
			_processedRecorder?.Resume();
			_status = "Texture-native GPU recording resumed.";
		}
	}

	public TextureNativeRecordingResult Stop()
	{
		lock (_stateLock)
		{
			if (_isStopping)
			{
				return CreateResult(SamplesWritten > 0, _status);
			}
			_isStopping = true;
		}
		_cancellation.Cancel();
		try
		{
			_reader.Flush(-4);
		}
		catch
		{
		}
		try
		{
			_recordingTask.Wait(RecordingStopTimeout);
		}
		catch
		{
		}
		_recorder?.Stop();
		_processedRecorder?.Stop();
		return CreateResult(SamplesWritten > 0 && File.Exists(Path) && new FileInfo(Path).Length > 4096, (SamplesWritten <= 0) ? _status : ((_processedRecorder != null) ? "Texture-native processed bridge recording completed." : "Texture-native GPU sample recording completed."));
	}

	public void Dispose()
	{
		Stop();
		_cancellation.Dispose();
		_recorder?.Dispose();
		_processedRecorder?.Dispose();
		MediaFoundationInterop.ReleaseComObject(_reader);
		MediaFoundationInterop.ReleaseComObject(_mediaSource);
		_deviceManager.Dispose();
		_mediaFoundationScope.Dispose();
	}

	private void CaptureLoop(CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				if (IsPaused)
				{
					Thread.Sleep(20);
					continue;
				}
				int actualStreamIndex;
				int streamFlags;
				long timestamp;
				object? sample;
				int num = _reader.ReadSample(-4, 0, out actualStreamIndex, out streamFlags, out timestamp, out sample);
				if (MediaFoundationInterop.Failed(num))
				{
					lock (_stateLock)
					{
						_status = $"Texture-native ReadSample failed: 0x{num:X8}";
						break;
					}
				}
				if ((streamFlags & 2) != 0)
				{
					lock (_stateLock)
					{
						_status = "Texture-native camera stream ended.";
						break;
					}
				}
				if (!(sample is IMFSample sample2))
				{
					MediaFoundationInterop.ReleaseComObject(sample);
					continue;
				}
				try
				{
					WriteRecordingSample(sample2);
				}
				finally
				{
					MediaFoundationInterop.ReleaseComObject(sample);
				}
			}
		}
		catch (Exception ex)
		{
			lock (_stateLock)
			{
				_status = ex.Message;
			}
		}
	}

	private void WriteRecordingSample(IMFSample sample)
	{
		if (_processedRecorder != null)
		{
			if (!TryCreateBgraFrame(sample, out byte[] bgraBytes))
			{
				return;
			}
			if (_recordingDenoiseApplied)
			{
				lock (_processedDenoiseLock)
				{
					VideoFrameDenoiser.ApplyTemporalDenoise(bgraBytes, _recordingDenoiseStrength, ref _previousProcessedDenoiseFrame);
				}
			}
			if (_recordingColorSettings.HasVisibleAdjustments)
			{
				VideoFrameColorProcessor.Apply(bgraBytes, _recordingColorSettings);
			}
			_processedRecorder.WriteFrame(bgraBytes);
		}
		else if (_recorder != null)
		{
			MediaFoundationInterop.ThrowIfFailed(sample.SetSampleTime(_nextSampleTime));
			MediaFoundationInterop.ThrowIfFailed(sample.SetSampleDuration(_sampleDuration));
			_recorder.WriteSample(sample);
		}
		_nextSampleTime += _sampleDuration;
	}

	private bool TryCreateBgraFrame(IMFSample sample, out byte[] bgraBytes)
	{
		bgraBytes = Array.Empty<byte>();
		IMFMediaBuffer? buffer = null;
		try
		{
			if (MediaFoundationInterop.Failed(sample.GetBufferByIndex(0, out buffer)) || buffer == null)
			{
				return false;
			}
			if (_subtype != MediaFoundationGuids.MFVideoFormat_NV12)
			{
				return false;
			}
			if (MediaFoundationInterop.Failed(buffer.Lock(out var buffer2, out var maxLength, out var currentLength)) || buffer2 == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				byte[]? array = Nv12FrameConverter.ConvertToBgra(buffer2, currentLength, _width, _height, out maxLength);
				if (array == null)
				{
					return false;
				}
				bgraBytes = array;
				return true;
			}
			finally
			{
				buffer.Unlock();
			}
		}
		finally
		{
			MediaFoundationInterop.ReleaseComObject(buffer);
		}
	}

	private TextureNativeRecordingResult CreateResult(bool success, string status)
	{
		long bytesWritten = (File.Exists(Path) ? new FileInfo(Path).Length : 0);
		return new TextureNativeRecordingResult(success, Path, SamplesWritten, bytesWritten, _deviceManager.ModeName, (_processedRecorder != null) ? "rgb32" : MediaFoundationInterop.FormatSubtype(_subtype), _width, _height, _fps, status, _recordingPipeline, _recordingDenoiseApplied, _recordingMatchesPreviewDenoise, _recordingColorPolishApplied, _recordingMatchesPreviewColor);
	}

	private static (int Width, int Height, double Fps, Guid Subtype) ReadCurrentFormat(IMFSourceReader reader, CameraVideoMode? requestedMode)
	{
		int item = requestedMode?.Width ?? 1280;
		int item2 = requestedMode?.Height ?? 720;
		double item3 = requestedMode?.FramesPerSecond ?? 30.0;
		Guid value = MediaFoundationGuids.MFVideoFormat_NV12;
		if (MediaFoundationInterop.Failed(reader.GetCurrentMediaType(-4, out IMFMediaType mediaType)))
		{
			return (Width: item, Height: item2, Fps: item3, Subtype: value);
		}
		try
		{
			if (MediaFoundationInterop.TryGetFrameSize(mediaType, out var width, out var height))
			{
				item = width;
				item2 = height;
			}
			if (MediaFoundationInterop.TryGetFrameRate(mediaType, out var framesPerSecond))
			{
				item3 = framesPerSecond;
			}
			mediaType.GetGUID(in MediaFoundationGuids.MF_MT_SUBTYPE, out value);
			return (Width: item, Height: item2, Fps: item3, Subtype: value);
		}
		finally
		{
			MediaFoundationInterop.ReleaseComObject(mediaType);
		}
	}
}
