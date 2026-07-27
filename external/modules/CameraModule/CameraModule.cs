using System;
using System.Threading;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectX12;
using AvatarBuilder.Modules.Webcam.Producer;

namespace AvatarBuilder.Modules.Webcam;

/// <summary>
/// Camera-only module. It owns capture and recording, publishes a retained
/// reference to the newest completed native frame, and has no viewport,
/// MediaPipe, identity, or overlay knowledge.
/// </summary>
public sealed class CameraModule :
	IModuleOutputSource<CameraOutput>,
	IFrameModuleTimingSource,
	IVisionModule,
	IDisposable
{
	private readonly ModuleOutputBroadcaster<CameraOutput> _outputs =
		new();

	private readonly TextureNativeCameraStream _stream;

	private TextureNativeFrameInfo? _latestInfo;

	private string _status = "DX12 webcam starting";

	private long _publishedFrames;

	private long _publicationDrops;

	private int _disposed;

	private int _started;

	public long FramesRead => _stream.FramesRead;

	public long FramesDroppedBeforePublication =>
		_stream.FramesDroppedWhileProcessingBusy;

	public long PublishedFrames => Interlocked.Read(ref _publishedFrames);

	public long PublicationDrops => Interlocked.Read(ref _publicationDrops);

	public long LastSourceFrameTimestamp =>
		_stream.LastSourceFrameTimestamp;

	public int Width => _stream.Width;

	public int Height => _stream.Height;

	public double FramesPerSecond => _stream.FramesPerSecond;

	public string DeviceMode => _stream.DeviceMode;

	public string MediaSubtype => _stream.MediaSubtype;

	public string Status => Volatile.Read(ref _status);

	public TimeSpan TimeWaited => _stream.TimeWaited;

	public TimeSpan TimeWorked => _stream.TimeWorked;

	public TextureNativeFrameInfo? LatestFrameInfo =>
		Volatile.Read(ref _latestInfo);

	public bool IsRecording => _stream.IsRecording;

	public int SamplesWritten => _stream.SamplesWritten;

	public CameraModule(
		CameraDevice camera,
		CameraVideoMode? mode = null)
	{
		ArgumentNullException.ThrowIfNull(camera);
		_stream = new TextureNativeCameraStream(
			camera,
			mode ?? CameraVideoMode.Auto,
			startImmediately: false);
		_stream.FrameAvailable += StreamFrameAvailable;
		_stream.TextureFrameAvailable += StreamTextureFrameAvailable;
		_stream.StatusChanged += StreamStatusChanged;
	}

	public void Start()
	{
		ObjectDisposedException.ThrowIf(
			Volatile.Read(ref _disposed) != 0,
			this);
		if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
		{
			_stream.Start();
		}
	}

	public TimeSpan GetIdleTime()
	{
		return TimeWaited;
	}

	public TimeSpan GetWorkingTime()
	{
		return TimeWorked;
	}

	public IModuleOutputSubscription<CameraOutput> Subscribe()
	{
		return _outputs.Subscribe();
	}

	public nint DuplicateNativeD3D12Device()
	{
		return _stream.DuplicateNativeD3D12Device();
	}

	public bool StartRecording(
		string path,
		TextureNativeRecordingOptions? options = null)
	{
		return _stream.StartRecording(path, options);
	}

	public void PauseRecording()
	{
		_stream.PauseRecording();
	}

	public void ResumeRecording()
	{
		_stream.ResumeRecording();
	}

	public TextureNativeRecordingResult? StopRecording()
	{
		return _stream.StopRecording();
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}

		_stream.FrameAvailable -= StreamFrameAvailable;
		_stream.TextureFrameAvailable -= StreamTextureFrameAvailable;
		_stream.StatusChanged -= StreamStatusChanged;
		try
		{
			_stream.Dispose();
		}
		finally
		{
			_outputs.Dispose();
		}
	}

	private void StreamFrameAvailable(
		object? sender,
		TextureNativeFrameInfo frame)
	{
		Volatile.Write(ref _latestInfo, frame);
	}

	private void StreamTextureFrameAvailable(
		object? sender,
		TextureNativeFrameLease frame)
	{
		if (Volatile.Read(ref _disposed) != 0)
		{
			return;
		}
		if (!_outputs.CanAcceptAny)
		{
			// The capture callback never waits and never retains a native
			// resource for a frame that cannot enter the chain.
			Interlocked.Increment(ref _publicationDrops);
			return;
		}

		TextureNativeFrameLease? retained = frame.Duplicate();
		if (retained is null)
		{
			Interlocked.Increment(ref _publicationDrops);
			return;
		}

		if (_outputs.Publish(new CameraOutput(retained)) > 0)
		{
			Interlocked.Increment(ref _publishedFrames);
		}
		else
		{
			Interlocked.Increment(ref _publicationDrops);
		}
	}

	private void StreamStatusChanged(object? sender, string status)
	{
		Volatile.Write(
			ref _status,
			string.IsNullOrWhiteSpace(status)
				? "DX12 webcam active"
				: status);
	}
}
