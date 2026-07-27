using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectX11;
using AvatarBuilder.Modules.Webcam.MediaFoundation;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed class TextureNativeCameraStream : IDisposable
{
	private sealed record AcceptedSourceSample(IMFSample Sample, long FrameNumber, long CapturedAtTimestamp, DateTime CapturedAtUtc);

	private static readonly TimeSpan StreamStartTimeout = TimeSpan.FromSeconds(8L);

	private static readonly TimeSpan StreamStopTimeout = TimeSpan.FromSeconds(3L);

	private readonly object _stateLock = new object();

	private readonly object _processedDenoiseLock = new object();

	private readonly object _recordingWriteLock = new object();

	private readonly CameraDevice _camera;

	private readonly CameraVideoMode? _mode;

	private readonly FrameModuleTiming _timing = new();

	private readonly ManualResetEventSlim _streamReady = new ManualResetEventSlim(initialState: false);

	private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

	private readonly object _frameWorkerLock = new object();

	private readonly AutoResetEvent _frameWorkerReady = new AutoResetEvent(initialState: false);

	private readonly object _recordingWorkerLock = new object();

	private readonly AutoResetEvent _recordingWorkerReady = new AutoResetEvent(initialState: false);

	private MediaFoundationCameraDeviceFactory.MediaFoundationScope? _mediaFoundationScope;

	private ITextureNativeDeviceManager? _deviceManager;

	private object? _mediaSource;

	private IMFSourceReader? _reader;

	private Thread? _captureThread;

	private Thread? _frameWorkerThread;

	private Thread? _recordingWorkerThread;

	private AcceptedSourceSample? _acceptedSourceSample;

	private AcceptedSourceSample? _acceptedRecordingSample;

	private int _frameWorkerBusy;

	private int _frameWorkerStopping;

	private int _recordingWorkerBusy;

	private int _recordingWorkerStopping;

	private int _recordingAccepting;

	private int _width;

	private int _height;

	private double _fps;

	private Guid _subtype;

	private long _sampleDuration;

	private MediaFoundationTextureVideoRecorder? _recorder;

	private MediaFoundationVideoRecorder? _processedRecorder;

	private Direct3D11SharedTextureBridge? _d3d11SharedTextureBridge;

	private bool _d3d11SharedTextureBridgeUnavailable;

	private byte[]? _previousProcessedDenoiseFrame;

	private string? _recordingPath;

	private string _recordingPipeline = "Media Foundation texture-native raw camera samples";

	private bool _recordingMatchesPreviewDenoise;

	private bool _recordingColorPolishApplied;

	private bool _recordingMatchesPreviewColor;

	private VideoFrameColorSettings _processedRecordingColorSettings = VideoFrameColorSettings.Off;

	private bool _processedRecordingDenoiseEnabled;

	private double _processedRecordingDenoiseStrength = 2.0;

	private bool _isPaused;

	private bool _isStopping;

	private long _nextSampleTime;

	private long _framesRead;

	private long _framesDroppedWhileProcessingBusy;

	private long _lastSourceFrameTimestamp;

	private int _frameInfoPublished;

	private string _status = "Texture-native camera stream started.";

	private bool _captureStarted;

	private Exception? _startException;

	public int Width => _width;

	public int Height => _height;

	public double FramesPerSecond => _fps;

	public TimeSpan TimeWaited => _timing.TimeWaited;

	public TimeSpan TimeWorked => _timing.TimeWorked;

	public string DeviceMode => _deviceManager?.ModeName ?? "starting";

	public string MediaSubtype => MediaFoundationInterop.FormatSubtype(_subtype);

	public long FramesRead => Interlocked.Read(in _framesRead);

	public long FramesDroppedWhileProcessingBusy => Interlocked.Read(in _framesDroppedWhileProcessingBusy);

	public long LastSourceFrameTimestamp => Volatile.Read(ref _lastSourceFrameTimestamp);

	public int SamplesWritten => _recorder?.SamplesWritten ?? _processedRecorder?.SamplesWritten ?? 0;

	public bool IsRecording
	{
		get
		{
			lock (_stateLock)
			{
				return _recorder != null || _processedRecorder != null;
			}
		}
	}

	public event EventHandler<TextureNativeFrameInfo>? FrameAvailable;

	public event EventHandler<TextureNativeFrameLease>? TextureFrameAvailable;

	public event EventHandler<string>? StatusChanged;

	public TextureNativeCameraStream(CameraDevice camera, CameraVideoMode? mode, bool startImmediately = true)
	{
		_camera = camera;
		_mode = mode;
		if (startImmediately)
		{
			Start();
		}
	}

	public nint DuplicateNativeD3D12Device()
	{
		return _deviceManager?.DuplicateNativeD3D12Device() ?? IntPtr.Zero;
	}

	public void Start()
	{
		lock (_stateLock)
		{
			if (_captureStarted || _isStopping)
			{
				return;
			}
			_captureStarted = true;
			_frameWorkerThread = new Thread(FrameWorkerLoop)
			{
				IsBackground = true,
				Name = "Avatar Builder newest camera frame",
				Priority = ThreadPriority.AboveNormal
			};
			_recordingWorkerThread = new Thread(RecordingWorkerLoop)
			{
				IsBackground = true,
				Name = "Avatar Builder camera recording",
				Priority = ThreadPriority.BelowNormal
			};
			_captureThread = new Thread(() => CaptureLoop(_cancellation.Token))
			{
				IsBackground = true,
				Name = "Avatar Builder camera ingestion",
				Priority = ThreadPriority.Highest
			};
			_recordingWorkerThread.Start();
			_frameWorkerThread.Start();
			_captureThread.Start();
		}
		if (!_streamReady.Wait(StreamStartTimeout))
		{
			throw new TimeoutException($"Texture-native stream did not initialize within {StreamStartTimeout.TotalSeconds:0.#} seconds.");
		}
		if (_startException != null)
		{
			throw new InvalidOperationException("Texture-native stream failed to initialize: " + _startException.Message, _startException);
		}
	}

	public bool StartRecording(string path, TextureNativeRecordingOptions? options = null)
	{
		lock (_stateLock)
		{
			ITextureNativeDeviceManager textureNativeDeviceManager = _deviceManager ?? throw new InvalidOperationException("Texture-native recording requires an initialized device manager.");
			if (_recorder != null || _processedRecorder != null)
			{
				return true;
			}
			if (options is not null && options.ProcessedOutputEnabled)
			{
				_processedRecorder = new MediaFoundationVideoRecorder(path, _width, _height, _fps, null);
				_processedRecordingDenoiseEnabled = options.DenoiseEnabled;
				_processedRecordingDenoiseStrength = options.DenoiseStrength;
				_processedRecordingColorSettings = options.ColorSettings;
				ResetProcessedDenoiseHistory();
				_recordingPipeline = "Texture-native processed BGRA bridge";
				_recordingMatchesPreviewDenoise = true;
				_recordingColorPolishApplied = options.ColorSettings.HasVisibleAdjustments;
				_recordingMatchesPreviewColor = true;
			}
			else
			{
				_recorder = new MediaFoundationTextureVideoRecorder(path, _width, _height, _fps, _subtype, textureNativeDeviceManager.Manager);
				_processedRecordingDenoiseEnabled = false;
				_processedRecordingColorSettings = VideoFrameColorSettings.Off;
				ResetProcessedDenoiseHistory();
				_recordingPipeline = "Media Foundation texture-native raw camera samples";
				_recordingMatchesPreviewDenoise = options is null || !options.DenoiseEnabled;
				_recordingColorPolishApplied = false;
				_recordingMatchesPreviewColor = options is null || !options.ColorSettings.HasVisibleAdjustments;
			}
			_recordingPath = path;
			_nextSampleTime = 0L;
			_isPaused = false;
			lock (_recordingWorkerLock)
			{
				Volatile.Write(ref _recordingAccepting, 1);
			}
			_status = "Texture-native GPU recording started: " + Path.GetFileName(path);
			this.StatusChanged?.Invoke(this, _status);
			return true;
		}
	}

	public void PauseRecording()
	{
		lock (_stateLock)
		{
			_isPaused = true;
			_processedRecorder?.Pause();
			_status = "Texture-native GPU recording paused.";
			this.StatusChanged?.Invoke(this, _status);
		}
	}

	public void ResumeRecording()
	{
		lock (_stateLock)
		{
			_isPaused = false;
			_processedRecorder?.Resume();
			_status = "Texture-native GPU recording resumed.";
			this.StatusChanged?.Invoke(this, _status);
		}
	}

	public TextureNativeRecordingResult? StopRecording()
	{
		lock (_recordingWorkerLock)
		{
			Volatile.Write(ref _recordingAccepting, 0);
		}
		WaitForRecordingWorkerIdle(StreamStopTimeout);
		lock (_recordingWriteLock)
		{
			return StopRecordingCore();
		}
	}

	private TextureNativeRecordingResult? StopRecordingCore()
	{
		MediaFoundationTextureVideoRecorder? recorder;
		MediaFoundationVideoRecorder? processedRecorder;
		string? recordingPath;
		string status;
		string recordingPipeline;
		bool processedRecordingDenoiseEnabled;
		bool recordingMatchesPreviewDenoise;
		bool recordingColorPolishApplied;
		bool recordingMatchesPreviewColor;
		lock (_stateLock)
		{
			recorder = _recorder;
			processedRecorder = _processedRecorder;
			recordingPath = _recordingPath;
			status = _status;
			recordingPipeline = _recordingPipeline;
			processedRecordingDenoiseEnabled = _processedRecordingDenoiseEnabled;
			recordingMatchesPreviewDenoise = _recordingMatchesPreviewDenoise;
			recordingColorPolishApplied = _recordingColorPolishApplied;
			recordingMatchesPreviewColor = _recordingMatchesPreviewColor;
			_recorder = null;
			_processedRecorder = null;
			_recordingPath = null;
			_isPaused = false;
			ResetProcessedDenoiseHistory();
			_processedRecordingDenoiseEnabled = false;
			_processedRecordingColorSettings = VideoFrameColorSettings.Off;
			_recordingMatchesPreviewDenoise = false;
			_recordingColorPolishApplied = false;
			_recordingMatchesPreviewColor = false;
			_recordingPipeline = "Media Foundation texture-native raw camera samples";
		}
		if ((recorder == null && processedRecorder == null) || string.IsNullOrWhiteSpace(recordingPath))
		{
			return null;
		}
		try
		{
			recorder?.Stop();
			processedRecorder?.Stop();
			long num = (File.Exists(recordingPath) ? new FileInfo(recordingPath).Length : 0);
			int num2 = recorder?.SamplesWritten ?? processedRecorder?.SamplesWritten ?? 0;
			int num3;
			object obj;
			if (num2 > 0)
			{
				num3 = ((num > 4096) ? 1 : 0);
				if (num3 != 0)
				{
					obj = ((processedRecorder != null) ? "Texture-native processed bridge recording completed." : "Texture-native shared stream recording completed.");
					goto IL_0137;
				}
			}
			else
			{
				num3 = 0;
			}
			obj = status;
			goto IL_0137;
			IL_0137:
			return new TextureNativeRecordingResult(Status: (string)obj, Success: (byte)num3 != 0, Path: recordingPath, SamplesWritten: num2, BytesWritten: num, DeviceMode: _deviceManager?.ModeName ?? "none", MediaSubtype: (processedRecorder != null) ? "rgb32" : MediaFoundationInterop.FormatSubtype(_subtype), Width: _width, Height: _height, FramesPerSecond: _fps, RecordingPipeline: recordingPipeline, RecordingDenoiseApplied: processedRecordingDenoiseEnabled, RecordingMatchesPreviewDenoise: recordingMatchesPreviewDenoise, RecordingColorPolishApplied: recordingColorPolishApplied, RecordingMatchesPreviewColor: recordingMatchesPreviewColor);
		}
		finally
		{
			recorder?.Dispose();
			processedRecorder?.Dispose();
		}
	}

	public void Stop()
	{
		lock (_stateLock)
		{
			if (_isStopping)
			{
				return;
			}
			_isStopping = true;
		}
		_cancellation.Cancel();
		TryFlushSourceReader();
		Thread? captureThread;
		lock (_stateLock)
		{
			captureThread = _captureThread;
		}
		bool flag = false;
		try
		{
			flag = captureThread?.Join(StreamStopTimeout) ?? true;
		}
		catch
		{
			flag = true;
		}
		if (!flag)
		{
			TryShutdownMediaSource();
			TryFlushSourceReader();
			try
			{
				captureThread?.Join(StreamStopTimeout);
			}
			catch
			{
			}
		}
		StopFrameWorker();
		StopRecording();
		StopRecordingWorker();
	}

	public void Dispose()
	{
		Stop();
		_cancellation.Dispose();
		_streamReady.Dispose();
		_frameWorkerReady.Dispose();
		_recordingWorkerReady.Dispose();
		TryShutdownMediaSource();
		MediaFoundationInterop.ReleaseComObject(_reader);
		_reader = null;
		MediaFoundationInterop.ReleaseComObject(_mediaSource);
		_mediaSource = null;
		_d3d11SharedTextureBridge?.Dispose();
		_d3d11SharedTextureBridge = null;
		_d3d11SharedTextureBridgeUnavailable = false;
		_deviceManager?.Dispose();
		_mediaFoundationScope?.Dispose();
	}

	private void TryShutdownMediaSource()
	{
		try
		{
			if (_mediaSource is IMFMediaSource iMFMediaSource)
			{
				iMFMediaSource.Shutdown();
			}
		}
		catch (COMException)
		{
		}
		catch (InvalidComObjectException)
		{
		}
	}

	private void CaptureLoop(CancellationToken cancellationToken)
	{
		try
		{
			_mediaFoundationScope = MediaFoundationCameraDeviceFactory.Startup();
			(ITextureNativeDeviceManager, IMFSourceReader, object) tuple = TextureNativeCameraRecorder.OpenTextureSourceReader(_camera, _mode);
			_deviceManager = tuple.Item1;
			_reader = tuple.Item2;
			_mediaSource = tuple.Item3;
			(int, int, double, Guid) tuple2 = ReadCurrentFormat(_reader, _mode);
			_width = tuple2.Item1;
			_height = tuple2.Item2;
			_fps = tuple2.Item3;
			_subtype = tuple2.Item4;
			_sampleDuration = Math.Max(1L, (long)Math.Round(10000000.0 / Math.Clamp(_fps, 1.0, 120.0)));
			_streamReady.Set();
			while (!cancellationToken.IsCancellationRequested)
			{
				IMFSourceReader reader = _reader;
				if (reader == null)
				{
					ReportStatus("Texture-native shared stream reader is not initialized.");
					break;
				}
				int actualStreamIndex;
				int streamFlags;
				long timestamp;
				object? sample;
				int num = reader.ReadSample(-4, 0, out actualStreamIndex, out streamFlags, out timestamp, out sample);
				if (MediaFoundationInterop.Failed(num))
				{
					ReportStatus($"Texture-native shared stream read failed: 0x{num:X8}");
					break;
				}
				if ((streamFlags & 2) != 0)
				{
					ReportStatus("Texture-native shared stream ended.");
					break;
				}
				if (!(sample is IMFSample sample2))
				{
					MediaFoundationInterop.ReleaseComObject(sample);
					continue;
				}
				long frameNumber = Interlocked.Increment(ref _framesRead);
				long capturedAtTimestamp = Stopwatch.GetTimestamp();
				DateTime capturedAtUtc = DateTime.UtcNow;
				Volatile.Write(ref _lastSourceFrameTimestamp, capturedAtTimestamp);
				if (TryAcceptNewestSourceSample(sample2, frameNumber, capturedAtTimestamp, capturedAtUtc))
				{
					sample = null;
				}
				else
				{
					Interlocked.Increment(ref _framesDroppedWhileProcessingBusy);
				}
				MediaFoundationInterop.ReleaseComObject(sample);
			}
		}
		catch (Exception ex)
		{
			if (_startException == null)
			{
				_startException = ex;
			}
			_streamReady.Set();
			ReportStatus(ex.Message);
		}
	}

	private bool TryAcceptNewestSourceSample(IMFSample sample, long frameNumber, long capturedAtTimestamp, DateTime capturedAtUtc)
	{
		if (Volatile.Read(ref _frameWorkerStopping) != 0
			|| Interlocked.CompareExchange(ref _frameWorkerBusy, 1, 0) != 0)
		{
			return false;
		}
		bool accepted = false;
		try
		{
			lock (_frameWorkerLock)
			{
				if (Volatile.Read(ref _frameWorkerStopping) == 0 && _acceptedSourceSample == null)
				{
					_timing.WorkStarted(Stopwatch.GetTimestamp());
					_acceptedSourceSample = new AcceptedSourceSample(sample, frameNumber, capturedAtTimestamp, capturedAtUtc);
					accepted = true;
				}
			}
			if (accepted)
			{
				_frameWorkerReady.Set();
			}
			return accepted;
		}
		finally
		{
			if (!accepted)
			{
				Interlocked.Exchange(ref _frameWorkerBusy, 0);
			}
		}
	}

	private void FrameWorkerLoop()
	{
		while (true)
		{
			_frameWorkerReady.WaitOne();
			AcceptedSourceSample? accepted;
			bool stopping;
			lock (_frameWorkerLock)
			{
				accepted = _acceptedSourceSample;
				_acceptedSourceSample = null;
				stopping = Volatile.Read(ref _frameWorkerStopping) != 0;
			}
			if (accepted == null)
			{
				Interlocked.Exchange(ref _frameWorkerBusy, 0);
				if (stopping)
				{
					break;
				}
				continue;
			}
			bool recordingOwnsSample = false;
			try
			{
				recordingOwnsSample = ProcessAcceptedSourceSample(accepted);
			}
			catch (Exception ex)
			{
				ReportStatus("Newest-frame camera worker skipped one frame: " + ex.Message);
			}
			finally
			{
				if (!recordingOwnsSample)
				{
					MediaFoundationInterop.ReleaseComObject(accepted.Sample);
				}
				Interlocked.Exchange(ref _frameWorkerBusy, 0);
			}
			if (stopping)
			{
				break;
			}
		}
	}

	private bool ProcessAcceptedSourceSample(AcceptedSourceSample accepted)
	{
		using TextureNativeFrameLease? frame = TryCreateFrameLease(
			accepted.Sample,
			accepted.FrameNumber,
			accepted.CapturedAtTimestamp,
			accepted.CapturedAtUtc);
		if (frame != null)
		{
			NotifyTextureFrameAvailable(frame);
			_timing.FrameMovedOut(Stopwatch.GetTimestamp());
		}
		if (Interlocked.CompareExchange(ref _frameInfoPublished, 1, 0) == 0)
		{
			NotifyFrameAvailable(new TextureNativeFrameInfo(
				_width,
				_height,
				_fps,
				_deviceManager?.ModeName ?? "DX12",
				MediaFoundationInterop.FormatSubtype(_subtype),
				accepted.FrameNumber));
		}
		return TryHandOffRecordingSample(accepted);
	}

	private bool TryHandOffRecordingSample(AcceptedSourceSample accepted)
	{
		if (Volatile.Read(ref _recordingAccepting) == 0
			|| Volatile.Read(ref _recordingWorkerStopping) != 0
			|| Interlocked.CompareExchange(ref _recordingWorkerBusy, 1, 0) != 0)
		{
			return false;
		}

		bool handedOff = false;
		try
		{
			lock (_recordingWorkerLock)
			{
				if (Volatile.Read(ref _recordingAccepting) != 0
					&& Volatile.Read(ref _recordingWorkerStopping) == 0
					&& _acceptedRecordingSample == null)
				{
					_acceptedRecordingSample = accepted;
					handedOff = true;
				}
			}
			if (handedOff)
			{
				_recordingWorkerReady.Set();
			}
			return handedOff;
		}
		finally
		{
			if (!handedOff)
			{
				Interlocked.Exchange(ref _recordingWorkerBusy, 0);
			}
		}
	}

	private void RecordingWorkerLoop()
	{
		while (true)
		{
			_recordingWorkerReady.WaitOne();
			AcceptedSourceSample? accepted;
			bool stopping;
			lock (_recordingWorkerLock)
			{
				accepted = _acceptedRecordingSample;
				_acceptedRecordingSample = null;
				stopping = Volatile.Read(ref _recordingWorkerStopping) != 0;
			}
			if (accepted == null)
			{
				Interlocked.Exchange(ref _recordingWorkerBusy, 0);
				if (stopping)
				{
					break;
				}
				continue;
			}
			try
			{
				lock (_recordingWriteLock)
				{
					WriteRecordingSample(accepted.Sample);
				}
			}
			catch (Exception ex)
			{
				ReportStatus("Recording lane skipped one frame after an encoder failure: " + ex.Message);
			}
			finally
			{
				MediaFoundationInterop.ReleaseComObject(accepted.Sample);
				Interlocked.Exchange(ref _recordingWorkerBusy, 0);
			}
			if (stopping)
			{
				break;
			}
		}
	}

	private void StopRecordingWorker()
	{
		if (Interlocked.Exchange(ref _recordingWorkerStopping, 1) == 0)
		{
			lock (_recordingWorkerLock)
			{
				Volatile.Write(ref _recordingAccepting, 0);
			}
			_recordingWorkerReady.Set();
		}

		Thread? worker = _recordingWorkerThread;
		if (worker != null && worker != Thread.CurrentThread)
		{
			worker.Join(StreamStopTimeout);
		}
		_recordingWorkerThread = null;
	}

	private void WaitForRecordingWorkerIdle(TimeSpan timeout)
	{
		long started = Stopwatch.GetTimestamp();
		while (Volatile.Read(ref _recordingWorkerBusy) != 0
			&& Stopwatch.GetElapsedTime(started) < timeout)
		{
			Thread.Sleep(1);
		}
	}

	private void StopFrameWorker()
	{
		if (Interlocked.Exchange(ref _frameWorkerStopping, 1) == 0)
		{
			_frameWorkerReady.Set();
		}
		Thread? worker = _frameWorkerThread;
		if (worker != null && worker != Thread.CurrentThread)
		{
			worker.Join(StreamStopTimeout);
		}
		_frameWorkerThread = null;
		Interlocked.Exchange(ref _frameWorkerBusy, 0);
	}

	private void TryFlushSourceReader()
	{
		try
		{
			_reader?.Flush(-4);
		}
		catch
		{
		}
	}

	private void WriteRecordingSample(IMFSample sample)
	{
		MediaFoundationTextureVideoRecorder? recorder;
		MediaFoundationVideoRecorder? processedRecorder;
		bool isPaused;
		bool processedRecordingDenoiseEnabled;
		double processedRecordingDenoiseStrength;
		VideoFrameColorSettings processedRecordingColorSettings;
		lock (_stateLock)
		{
			recorder = _recorder;
			processedRecorder = _processedRecorder;
			isPaused = _isPaused;
			processedRecordingDenoiseEnabled = _processedRecordingDenoiseEnabled;
			processedRecordingDenoiseStrength = _processedRecordingDenoiseStrength;
			processedRecordingColorSettings = _processedRecordingColorSettings;
		}
		if (isPaused || (recorder == null && processedRecorder == null))
		{
			return;
		}
		if (processedRecorder != null)
		{
			if (!TryCreateBgraFrame(sample, out byte[] bgraBytes))
			{
				return;
			}
			if (processedRecordingDenoiseEnabled)
			{
				lock (_processedDenoiseLock)
				{
					VideoFrameDenoiser.ApplyTemporalDenoise(bgraBytes, processedRecordingDenoiseStrength, ref _previousProcessedDenoiseFrame);
				}
			}
			else
			{
				ResetProcessedDenoiseHistory();
			}
			if (processedRecordingColorSettings.HasVisibleAdjustments)
			{
				VideoFrameColorProcessor.Apply(bgraBytes, processedRecordingColorSettings);
			}
			processedRecorder.WriteFrame(bgraBytes);
		}
		else if (recorder != null)
		{
			MediaFoundationInterop.ThrowIfFailed(sample.SetSampleTime(_nextSampleTime));
			MediaFoundationInterop.ThrowIfFailed(sample.SetSampleDuration(_sampleDuration));
			recorder.WriteSample(sample);
		}
		_nextSampleTime += _sampleDuration;
	}

	private void ReportStatus(string status)
	{
		lock (_stateLock)
		{
			_status = status;
		}
		NotifyStatusChanged(status);
	}

	private void NotifyFrameAvailable(TextureNativeFrameInfo frame)
	{
		EventHandler<TextureNativeFrameInfo>? eventHandler = this.FrameAvailable;
		if (eventHandler == null)
		{
			return;
		}
		Delegate[] invocationList = eventHandler.GetInvocationList();
		foreach (Delegate obj in invocationList)
		{
			try
			{
				((EventHandler<TextureNativeFrameInfo>)obj)(this, frame);
			}
			catch (Exception exception)
			{
				SetObserverFailureStatus("frame", exception);
			}
		}
	}

	private void NotifyTextureFrameAvailable(TextureNativeFrameLease frame)
	{
		EventHandler<TextureNativeFrameLease>? eventHandler = this.TextureFrameAvailable;
		if (eventHandler == null)
		{
			return;
		}
		Delegate[] invocationList = eventHandler.GetInvocationList();
		foreach (Delegate obj in invocationList)
		{
			try
			{
				((EventHandler<TextureNativeFrameLease>)obj)(this, frame);
			}
			catch (Exception exception)
			{
				SetObserverFailureStatus("texture", exception);
			}
		}
	}

	private void SetObserverFailureStatus(string observer, Exception exception)
	{
		lock (_stateLock)
		{
			_status = "Texture-native " + observer + " observer failed: " + exception.Message;
		}
		NotifyStatusChanged(_status);
	}

	private void NotifyStatusChanged(string status)
	{
		EventHandler<string>? eventHandler = this.StatusChanged;
		if (eventHandler == null)
		{
			return;
		}
		Delegate[] invocationList = eventHandler.GetInvocationList();
		foreach (Delegate obj in invocationList)
		{
			try
			{
				((EventHandler<string>)obj)(this, status);
			}
			catch
			{
			}
		}
	}

	private void ResetProcessedDenoiseHistory()
	{
		lock (_processedDenoiseLock)
		{
			_previousProcessedDenoiseFrame = null;
		}
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

	private TextureNativeFrameLease? TryCreateFrameLease(IMFSample sample, long frameNumber, long capturedAtTimestamp, DateTime capturedAtUtc)
	{
		IMFMediaBuffer? buffer = null;
		PooledFrameBuffer? pooledFrameBuffer = null;
		try
		{
			if (MediaFoundationInterop.Failed(sample.GetBufferByIndex(0, out buffer)) || buffer == null)
			{
				return null;
			}
			int nv12Stride = 0;
			IMFDXGIBuffer? iMFDXGIBuffer = QueryDxgiBuffer(buffer);
			if (iMFDXGIBuffer == null)
			{
				pooledFrameBuffer = TryCreateNv12Preview(
					buffer,
					out nv12Stride);
				if (pooledFrameBuffer == null)
				{
					return null;
				}
				TextureNativeFrameLease result = new TextureNativeFrameLease(IntPtr.Zero, 0, _width, _height, _fps, (_deviceManager?.ModeName ?? "DX12") + " NV12 upload", MediaFoundationInterop.FormatSubtype(_subtype), frameNumber, IntPtr.Zero, pooledFrameBuffer, nv12Stride, capturedAtTimestamp, capturedAtUtc);
				pooledFrameBuffer = null;
				return result;
			}
			try
			{
				ITextureNativeDeviceManager? deviceManager = _deviceManager;
				if (deviceManager == null)
				{
					return null;
				}
				int subresource = 0;
				iMFDXGIBuffer.GetSubresourceIndex(out subresource);
				if (MediaFoundationInterop.Failed(iMFDXGIBuffer.GetResource(deviceManager.TextureResourceId, out var resource)) || resource == IntPtr.Zero)
				{
					return null;
				}
				nint num = IntPtr.Zero;
				Direct3D11SharedTextureFrameLease? sharedTextureFrame = null;
				try
				{
					string mediaSubtype = MediaFoundationInterop.FormatSubtype(_subtype);
					if (deviceManager is Direct3D11DeviceManager)
					{
						sharedTextureFrame = TryCreateD3D11SharedTextureFrame(
							resource,
							subresource,
							deviceManager,
							out bool bridgeUnavailable);
						if (sharedTextureFrame == null && !bridgeUnavailable)
						{
							Marshal.Release(resource);
							return null;
						}
						if (sharedTextureFrame == null)
						{
							pooledFrameBuffer = TryCreateNv12Preview(
								buffer,
								out nv12Stride);
						}
					}
					TextureNativeFrameLease result2 = new TextureNativeFrameLease(resource, subresource, _width, _height, _fps, deviceManager.ModeName, mediaSubtype, frameNumber, num, pooledFrameBuffer, nv12Stride, capturedAtTimestamp, capturedAtUtc, sharedTextureFrame);
					sharedTextureFrame = null;
					pooledFrameBuffer = null;
					return result2;
				}
				catch
				{
					sharedTextureFrame?.Dispose();
					pooledFrameBuffer?.Dispose();
					Marshal.Release(resource);
					TextureNativeFrameLease.CloseOwnedSharedTextureHandle(num);
					throw;
				}
			}
			finally
			{
				MediaFoundationInterop.ReleaseComObject(iMFDXGIBuffer);
			}
		}
		finally
		{
			pooledFrameBuffer?.Dispose();
			MediaFoundationInterop.ReleaseComObject(buffer);
		}
	}

	private Direct3D11SharedTextureFrameLease? TryCreateD3D11SharedTextureFrame(
		nint sourceTexture,
		int sourceSubresource,
		ITextureNativeDeviceManager deviceManager,
		out bool bridgeUnavailable)
	{
		bridgeUnavailable = _d3d11SharedTextureBridgeUnavailable;
		if (_subtype != MediaFoundationGuids.MFVideoFormat_NV12 || !(deviceManager is Direct3D11DeviceManager direct3D11DeviceManager) || _d3d11SharedTextureBridgeUnavailable)
		{
			return null;
		}
		try
		{
			if (_d3d11SharedTextureBridge == null)
			{
				_d3d11SharedTextureBridge = direct3D11DeviceManager.CreateSharedTextureBridge(_width, _height);
			}
			if (_d3d11SharedTextureBridge.TryCopyToSharedFrame(
				sourceTexture,
				sourceSubresource,
				out Direct3D11SharedTextureFrameLease? frame,
				out string? failureReason))
			{
				return frame;
			}
			if (!string.IsNullOrWhiteSpace(failureReason))
			{
				DisableD3D11SharedTextureBridge(failureReason);
				bridgeUnavailable = true;
			}
		}
		catch (Exception ex)
		{
			DisableD3D11SharedTextureBridge(ex.Message);
			bridgeUnavailable = true;
		}
		return null;
	}

	private void DisableD3D11SharedTextureBridge(string? reason)
	{
		if (!_d3d11SharedTextureBridgeUnavailable)
		{
			_d3d11SharedTextureBridgeUnavailable = true;
			_d3d11SharedTextureBridge?.Dispose();
			_d3d11SharedTextureBridge = null;
			ReportStatus("D3D11 shared texture bridge unavailable: " + (reason ?? "unknown failure"));
		}
	}

	private PooledFrameBuffer? TryCreateNv12Preview(IMFMediaBuffer buffer, out int nv12Stride)
	{
		nv12Stride = 0;
		if (_subtype != MediaFoundationGuids.MFVideoFormat_NV12)
		{
			return null;
		}
		if (MediaFoundationInterop.Failed(buffer.Lock(out var buffer2, out var _, out var currentLength)) || buffer2 == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			int num = Math.Max(_width, currentLength * 2 / Math.Max(1, _height * 3));
			int num2 = num * _height + num * ((_height + 1) / 2);
			if (currentLength < num2)
			{
				return null;
			}
			PooledFrameBuffer pooledFrameBuffer = PooledFrameBuffer.Rent(num2);
			try
			{
				Marshal.Copy(buffer2, pooledFrameBuffer.Bytes, 0, num2);
				nv12Stride = num;
				return pooledFrameBuffer;
			}
			catch
			{
				pooledFrameBuffer.Dispose();
				throw;
			}
		}
		finally
		{
			buffer.Unlock();
		}
	}

	private static IMFDXGIBuffer? QueryDxgiBuffer(IMFMediaBuffer buffer)
	{
		nint num = IntPtr.Zero;
		nint ppv = IntPtr.Zero;
		try
		{
			num = Marshal.GetIUnknownForObject(buffer);
			if (Marshal.QueryInterface(num, typeof(IMFDXGIBuffer).GUID, out ppv) < 0 || ppv == IntPtr.Zero)
			{
				return null;
			}
			return (IMFDXGIBuffer)Marshal.GetObjectForIUnknown(ppv);
		}
		finally
		{
			if (ppv != IntPtr.Zero)
			{
				Marshal.Release(ppv);
			}
			if (num != IntPtr.Zero)
			{
				Marshal.Release(num);
			}
		}
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
