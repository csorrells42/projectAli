using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectX12;
using Vortice;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace AvatarBuilder.Modules.Viewports.DirectX12;

public sealed class Direct3D12PreviewHost : DirectX12ViewportHost
{
	private sealed record AcceptedCameraFrame(CameraFrame Frame, VideoFrameColorSettings ColorSettings, bool DenoiseEnabled, double DenoiseStrength, long FrameNumber) : IDisposable
	{
		public byte[] BgraBytes => Frame.BgraBytes;

		public int Width => Frame.Width;

		public int Height => Frame.Height;

		public int Stride => Frame.Stride;

		public byte[]? Nv12Bytes => Frame.Nv12Bytes;

		public int Nv12Stride => Frame.Nv12Stride;

		public string FrameFormat => Frame.Format;

		public void Dispose()
		{
			Frame.Dispose();
		}
	}

	private sealed record AcceptedTextureFrame(TextureNativeFrameLease Frame, VideoFrameColorSettings ColorSettings, bool DenoiseEnabled, double DenoiseStrength, string? SharedBridgeFailureReason, TexturePreviewRead PreviewRead, PreviewOverlayStack Overlays) : IDisposable
	{
		public void Dispose()
		{
			Frame.Dispose();
		}
	}

	private static readonly TimeSpan RendererDisposeLockTimeout = TimeSpan.FromMilliseconds(250L);

	private static readonly TimeSpan GpuOperationTimeout = TimeSpan.FromSeconds(2L);

	private static readonly TimeSpan TextureSubmissionTimeout = TimeSpan.FromSeconds(2L);

	private static readonly TimeSpan RenderWorkerStopTimeout = TimeSpan.FromMilliseconds(500L);

	private nint _nativeD3D12Device;

	private readonly object _rendererLock = new object();

	private readonly object _renderWorkerLock = new object();

	private readonly object _renderThrottleLock = new object();

	private readonly AutoResetEvent _renderFrameReady = new AutoResetEvent(initialState: false);

	private readonly ConcurrentDictionary<long, TexturePreviewRead>
		_texturePreviewReads = new();

	private Direct3D12SwapChainRenderer? _renderer;

	private Thread? _renderThread;

	private AcceptedCameraFrame? _acceptedCameraFrame;

	private AcceptedTextureFrame? _acceptedTextureFrame;

	private string _previewPathDescription = "DX12 preview path pending";

	private readonly object _diagnosticsLock = new object();

	private Direct3D12PreviewDiagnostics _diagnostics = Direct3D12PreviewDiagnostics.Empty;

	private long _diagnosticsFpsWindowStartTimestamp = Stopwatch.GetTimestamp();

	private long _diagnosticsFpsWindowStartRenderedFrames;

	private long _submittedFrames;

	private long _renderedFrames;

	private long _droppedFrames;

	private long _lastRenderedFrameTimestamp;

	private double _renderFramesPerSecond;

	private string _recordingMode = "not recording";

	private long _lastAcceptedRenderFrameTimestamp;

	private long _lastDiagnosticsPublishedTimestamp;

	private double _maxRenderFramesPerSecond;

	private int _renderingSuspended;

	private int _renderFrameBusy;

	private bool _renderWorkerStopping;

	private bool _disposed;

	public bool IsReady => _renderer != null;

	public string DeviceDescription => _renderer?.DeviceDescription ?? "DX12 preview not initialized";

	public string PreviewPathDescription => _previewPathDescription;

	public string RecordingMode => Volatile.Read(in _recordingMode);

	public long SubmittedFrames => Interlocked.Read(ref _submittedFrames);

	public long RenderedFrames => Interlocked.Read(ref _renderedFrames);

	public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

	public long LastRenderedFrameTimestamp =>
		Volatile.Read(ref _lastRenderedFrameTimestamp);

	public double RenderFramesPerSecond => Volatile.Read(ref _renderFramesPerSecond);

	public Direct3D12PreviewDiagnostics Diagnostics
	{
		get
		{
			lock (_diagnosticsLock)
			{
				return _diagnostics;
			}
		}
	}

	private bool IsRenderingSuspended => Volatile.Read(in _renderingSuspended) != 0;

	public event EventHandler<string>? StatusChanged;

	public event EventHandler<Direct3D12PreviewDiagnostics>? DiagnosticsChanged;

	public Direct3D12PreviewHost(nint nativeD3D12Device = 0)
		: base("Could not create DX12 preview child window.")
	{
		_nativeD3D12Device = nativeD3D12Device;
		_renderThread = new Thread(RenderWorkerLoop)
		{
			IsBackground = true,
			Name = "Avatar Builder DX12 preview",
			Priority = ThreadPriority.AboveNormal
		};
		_renderThread.Start();
	}

	public void SetRecordingMode(string recordingMode)
	{
		Volatile.Write(ref _recordingMode, string.IsNullOrWhiteSpace(recordingMode) ? "not recording" : recordingMode.Trim());
	}

	public void LimitRenderRate(double maxFramesPerSecond)
	{
		lock (_renderThrottleLock)
		{
			_maxRenderFramesPerSecond = ((maxFramesPerSecond <= 0.0) ? 0.0 : Math.Clamp(maxFramesPerSecond, 1.0, 120.0));
			_lastAcceptedRenderFrameTimestamp = 0L;
		}
	}

	private sealed class TexturePreviewRead : IDisposable
	{
		private readonly ManualResetEventSlim _submitted = new(false);

		private long _fenceValue;

		private int _published;

		private int _discarded;

		private int _disposed;

		public void Publish(ulong fenceValue)
		{
			if (Interlocked.Exchange(ref _published, 1) != 0)
			{
				return;
			}
			Volatile.Write(ref _fenceValue, checked((long)fenceValue));
			try
			{
				_submitted.Set();
			}
			catch (ObjectDisposedException)
			{
				return;
			}
			if (Volatile.Read(ref _discarded) != 0)
			{
				Dispose();
			}
		}

		public void Discard()
		{
			if (Interlocked.Exchange(ref _discarded, 1) == 0
				&& Volatile.Read(ref _published) != 0)
			{
				Dispose();
			}
		}

		public bool TryWaitForSubmission(
			TimeSpan timeout,
			out ulong fenceValue)
		{
			fenceValue = 0uL;
			try
			{
				if (!_submitted.Wait(timeout))
				{
					return false;
				}
			}
			catch (ObjectDisposedException)
			{
				return false;
			}
			fenceValue = checked((ulong)Math.Max(
				0L,
				Volatile.Read(ref _fenceValue)));
			return true;
		}

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
			{
				_submitted.Dispose();
			}
		}
	}

	public void RenderBgraFrame(CameraFrame frame, long frameNumber, VideoFrameColorSettings colorSettings = default(VideoFrameColorSettings), bool denoiseEnabled = false, double denoiseStrength = 0.0)
	{
		if (_disposed || IsRenderingSuspended)
		{
			return;
		}
		RecordSubmittedFrame();
		if (!ShouldAcceptRenderFrame())
		{
			RecordDroppedFrame();
			return;
		}
		if (Interlocked.CompareExchange(ref _renderFrameBusy, 1, 0) != 0)
		{
			RecordDroppedFrame();
			return;
		}
		AcceptedCameraFrame? acceptedCameraFrame = null;
		bool flag = false;
		try
		{
			acceptedCameraFrame = new AcceptedCameraFrame(frame.Duplicate(), colorSettings, denoiseEnabled, denoiseStrength, frameNumber);
			lock (_renderWorkerLock)
			{
				if (_renderWorkerStopping || _acceptedCameraFrame is not null || _acceptedTextureFrame is not null)
				{
					RecordDroppedFrame();
					return;
				}
				_acceptedCameraFrame = acceptedCameraFrame;
				acceptedCameraFrame = null;
				flag = true;
			}
			_renderFrameReady.Set();
		}
		catch (ObjectDisposedException)
		{
			RecordDroppedFrame();
			CancelAcceptedRenderHandoff();
			flag = false;
		}
		catch
		{
			RecordDroppedFrame();
		}
		finally
		{
			acceptedCameraFrame?.Dispose();
			if (!flag)
			{
				Interlocked.Exchange(ref _renderFrameBusy, 0);
			}
		}
	}

	public void RenderProofFrame(TextureNativeFrameInfo frame)
	{
		if (_renderer == null || IsRenderingSuspended)
		{
			return;
		}
		try
		{
			lock (_rendererLock)
			{
				_renderer.RenderProofFrame(frame.FrameNumber);
			}
		}
		catch (Exception ex)
		{
			this.StatusChanged?.Invoke(this, "DX12 preview render failed: " + ex.Message);
		}
	}

	public void RenderTextureFrame(
		TextureNativeFrameLease frame,
		bool denoiseEnabled,
		double denoiseStrength,
		VideoFrameColorSettings colorSettings =
			default(VideoFrameColorSettings),
		PreviewOverlayStack? overlays = null)
	{
		RecordSubmittedFrame();
		if (_disposed || IsRenderingSuspended || !ShouldAcceptRenderFrame())
		{
			RecordDroppedFrame();
			return;
		}
		if (Interlocked.CompareExchange(ref _renderFrameBusy, 1, 0) != 0)
		{
			RecordDroppedFrame();
			return;
		}
		bool flag = false;
		bool flag2 = string.Equals(frame.DeviceMode, "D3D12", StringComparison.OrdinalIgnoreCase);
		bool flag3 = TextureNativePreviewPolicy.ShouldPreferNv12UploadFallback(frame.MediaSubtype, frame.Width, frame.Height, frame.Nv12PreviewBytes, frame.Nv12PreviewStride);
		string? failureReason = !flag2
			&& !flag3
			&& frame.D3D12SharedTextureHandle == IntPtr.Zero
				? "D3D11 bridge shared texture handle missing"
				: null;
		TextureNativeFrameLease? textureNativeFrameLease = null;
		TexturePreviewRead? previewRead = null;
		try
		{
			textureNativeFrameLease = frame.Duplicate();
			if (textureNativeFrameLease == null)
			{
				RecordDroppedFrame();
				return;
			}
			lock (_renderWorkerLock)
			{
				if (_renderWorkerStopping || _acceptedCameraFrame is not null || _acceptedTextureFrame is not null)
				{
					RecordDroppedFrame();
					return;
				}
				previewRead = new TexturePreviewRead();
				if (_texturePreviewReads.TryRemove(
					frame.FrameNumber,
					out TexturePreviewRead? previousRead))
				{
					previousRead.Dispose();
				}
				_texturePreviewReads[frame.FrameNumber] = previewRead;
				_acceptedTextureFrame = new AcceptedTextureFrame(
					textureNativeFrameLease,
					colorSettings,
					denoiseEnabled,
					denoiseStrength,
					failureReason,
					previewRead,
					overlays ?? PreviewOverlayStack.Empty);
				textureNativeFrameLease = null;
				previewRead = null;
				flag = true;
			}
			_renderFrameReady.Set();
		}
		catch (ObjectDisposedException)
		{
			RecordDroppedFrame();
			CancelAcceptedRenderHandoff();
			flag = false;
		}
		finally
		{
			previewRead?.Dispose();
			textureNativeFrameLease?.Dispose();
			if (!flag)
			{
				Interlocked.Exchange(ref _renderFrameBusy, 0);
			}
		}
	}

	public void ResumeRendering()
	{
		if (!_disposed)
		{
			lock (_renderThrottleLock)
			{
				_lastAcceptedRenderFrameTimestamp = 0L;
			}
			Interlocked.Exchange(ref _renderingSuspended, 0);
			Volatile.Read(in _renderer)?.RequestPresentationRefresh();
			_renderFrameReady.Set();
		}
	}

	public void SuspendRendering()
	{
		if (_disposed)
		{
			return;
		}
		Interlocked.Exchange(ref _renderingSuspended, 1);
		lock (_renderWorkerLock)
		{
			bool num = _acceptedCameraFrame is not null || _acceptedTextureFrame is not null;
			_acceptedCameraFrame?.Dispose();
			_acceptedCameraFrame = null;
			_acceptedTextureFrame?.PreviewRead.Publish(0uL);
			_acceptedTextureFrame?.Dispose();
			_acceptedTextureFrame = null;
			if (num)
			{
				Interlocked.Exchange(ref _renderFrameBusy, 0);
			}
		}
		_renderFrameReady.Set();
		if (Monitor.TryEnter(_rendererLock, TimeSpan.FromMilliseconds(100L)))
		{
			Monitor.Exit(_rendererLock);
		}
	}

	private void RenderTextureFrameCore(AcceptedTextureFrame acceptedFrame)
	{
		TextureNativeFrameLease frame = acceptedFrame.Frame;
		if (IsRenderingSuspended)
		{
			RecordDroppedFrame();
			acceptedFrame.PreviewRead.Publish(0uL);
			return;
		}
		ulong submittedFenceValue = 0uL;
		try
		{
			lock (_rendererLock)
			{
				if (_renderer == null || IsRenderingSuspended)
				{
					RecordDroppedFrame();
					return;
				}
			string? failureReason = null;
				if (frame.IsValid && string.Equals(frame.DeviceMode, "D3D12", StringComparison.OrdinalIgnoreCase) && _renderer.RenderNativeTextureFrame(frame, acceptedFrame.ColorSettings, acceptedFrame.DenoiseEnabled, acceptedFrame.DenoiseStrength, acceptedFrame.Overlays, out failureReason))
				{
					ReportPreviewPath("direct DX12 texture");
					RecordRenderedFrame("direct DX12 texture", frame.MediaSubtype, frame.Width, frame.Height, frame.FramesPerSecond, acceptedFrame.DenoiseEnabled, acceptedFrame.DenoiseStrength, acceptedFrame.ColorSettings, RecordingMode, null, frame.FrameNumber);
					submittedFenceValue =
						_renderer.LastSubmittedFenceValue;
					return;
				}
				if (_renderer.LastRenderAttemptWasBusy)
				{
					RecordDroppedFrame();
					return;
				}
				bool preferNv12Upload =
					TextureNativePreviewPolicy.ShouldPreferNv12UploadFallback(
						frame.MediaSubtype,
						frame.Width,
						frame.Height,
						frame.Nv12PreviewBytes,
						frame.Nv12PreviewStride);
				string? sharedBridgeFailureReason = null;
				if (!string.Equals(
						frame.DeviceMode,
						"D3D12",
						StringComparison.OrdinalIgnoreCase)
					&& !preferNv12Upload
					&& frame.D3D12SharedTextureHandle != IntPtr.Zero
					&& _renderer.RenderSharedD3D11BridgeFrame(
						frame,
						acceptedFrame.ColorSettings,
						acceptedFrame.DenoiseEnabled,
						acceptedFrame.DenoiseStrength,
						acceptedFrame.Overlays,
						out sharedBridgeFailureReason))
				{
					ReportPreviewPath(
						"DX12 D3D11 bridge texture preview");
					RecordRenderedFrame(
						"DX12 D3D11 bridge texture preview",
						frame.MediaSubtype,
						frame.Width,
						frame.Height,
						frame.FramesPerSecond,
						acceptedFrame.DenoiseEnabled,
						acceptedFrame.DenoiseStrength,
						acceptedFrame.ColorSettings,
						RecordingMode,
						null,
						frame.FrameNumber);
					submittedFenceValue =
						_renderer.LastSubmittedFenceValue;
					return;
				}
				if (_renderer.LastRenderAttemptWasBusy)
				{
					RecordDroppedFrame();
					return;
				}
				if (!string.IsNullOrWhiteSpace(
					sharedBridgeFailureReason))
				{
					failureReason = sharedBridgeFailureReason;
				}
			string? text = CombineTextureFailureReasons(failureReason, acceptedFrame.SharedBridgeFailureReason);
				if (!TryRenderNv12TextureUpload(_renderer, frame, text, acceptedFrame.ColorSettings, acceptedFrame.DenoiseEnabled, acceptedFrame.DenoiseStrength, acceptedFrame.Overlays))
				{
					RecordDroppedFrame();
				}
			}
		}
		catch (Exception ex)
		{
			RecordDroppedFrame();
			this.StatusChanged?.Invoke(this, "DX12 camera frame upload failed: " + ex.Message);
		}
		finally
		{
			acceptedFrame.PreviewRead.Publish(submittedFenceValue);
		}
	}

	private void ReportPreviewPath(string description)
	{
		if (!string.Equals(_previewPathDescription, description, StringComparison.Ordinal))
		{
			_previewPathDescription = description;
			this.StatusChanged?.Invoke(this, "DX12 preview path: " + description);
		}
	}

	private void RenderWorkerLoop()
	{
		try
		{
			while (true)
			{
				_renderFrameReady.WaitOne();
				AcceptedTextureFrame? acceptedTextureFrame;
				AcceptedCameraFrame? acceptedCameraFrame;
				lock (_renderWorkerLock)
				{
					if (_renderWorkerStopping)
					{
						break;
					}
					acceptedTextureFrame = _acceptedTextureFrame;
					_acceptedTextureFrame = null;
					acceptedCameraFrame = ((acceptedTextureFrame is null) ? _acceptedCameraFrame : null);
					_acceptedCameraFrame = null;
				}
				try
				{
					if (acceptedTextureFrame is not null)
					{
						using (acceptedTextureFrame)
						{
							RenderTextureFrameCore(acceptedTextureFrame);
						}
					}
					else
					{
						if (acceptedCameraFrame is null)
						{
							continue;
						}
						using (acceptedCameraFrame)
						{
							try
							{
								lock (_rendererLock)
								{
									if (_renderer != null && !IsRenderingSuspended)
									{
								byte[]? nv12Bytes = acceptedCameraFrame.Nv12Bytes;
										if (nv12Bytes == null || nv12Bytes.Length <= 0 || acceptedCameraFrame.Nv12Stride <= 0)
										{
											goto IL_0216;
										}
										if (!_renderer.RenderNv12Frame(nv12Bytes, acceptedCameraFrame.Width, acceptedCameraFrame.Height, acceptedCameraFrame.Nv12Stride, acceptedCameraFrame.FrameNumber, acceptedCameraFrame.ColorSettings, acceptedCameraFrame.DenoiseEnabled, acceptedCameraFrame.DenoiseStrength, PreviewOverlayStack.Empty, acceptedCameraFrame.FrameFormat == "nv12-ffmpeg"))
										{
											if (_renderer.LastRenderAttemptWasBusy)
											{
												RecordDroppedFrame();
												continue;
											}
											this.StatusChanged?.Invoke(this, $"DX12 NV12 preview renderer refused {acceptedCameraFrame.Width}x{acceptedCameraFrame.Height}, stride {acceptedCameraFrame.Nv12Stride}, bytes {nv12Bytes.Length}: {_renderer.LastNv12PreviewFailureReason}");
											goto IL_0216;
										}
										ReportPreviewPath("DX12 NV12 upload preview");
										RecordRenderedFrame("DX12 NV12 upload preview", acceptedCameraFrame.FrameFormat, acceptedCameraFrame.Width, acceptedCameraFrame.Height, 0.0, acceptedCameraFrame.DenoiseEnabled, acceptedCameraFrame.DenoiseStrength, acceptedCameraFrame.ColorSettings, RecordingMode, null, acceptedCameraFrame.FrameNumber);
									}
									goto end_IL_0085;
									IL_0216:
									if (acceptedCameraFrame.BgraBytes.Length == 0 || acceptedCameraFrame.Stride <= 0)
									{
										this.StatusChanged?.Invoke(this, "DX12 preview skipped: frame had no renderable BGRA or NV12 payload.");
										continue;
									}
									if (!_renderer.RenderBgraFrame(acceptedCameraFrame.BgraBytes, acceptedCameraFrame.Width, acceptedCameraFrame.Height, acceptedCameraFrame.Stride, acceptedCameraFrame.FrameNumber, acceptedCameraFrame.ColorSettings, acceptedCameraFrame.DenoiseEnabled, acceptedCameraFrame.DenoiseStrength, PreviewOverlayStack.Empty))
									{
										RecordDroppedFrame();
										continue;
									}
									goto IL_0296;
									end_IL_0085:;
								}
								goto end_IL_007c;
								IL_0296:
								ReportPreviewPath("DX12 BGRA upload preview");
								RecordRenderedFrame("DX12 BGRA upload preview", acceptedCameraFrame.FrameFormat, acceptedCameraFrame.Width, acceptedCameraFrame.Height, 0.0, acceptedCameraFrame.DenoiseEnabled, acceptedCameraFrame.DenoiseStrength, acceptedCameraFrame.ColorSettings, RecordingMode, null, acceptedCameraFrame.FrameNumber);
								end_IL_007c:;
							}
							catch (Exception ex)
							{
								RecordDroppedFrame();
								this.StatusChanged?.Invoke(this, "DX12 BGRA preview upload failed: " + ex.Message);
							}
						}
						continue;
					}
				}
				finally
				{
					Interlocked.Exchange(ref _renderFrameBusy, 0);
				}
			}
		}
		finally
		{
			DisposeRenderer();
		}
	}

	private bool StopRenderWorker()
	{
		lock (_renderWorkerLock)
		{
			_renderWorkerStopping = true;
			bool num = _acceptedCameraFrame is not null || _acceptedTextureFrame is not null;
			_acceptedCameraFrame?.Dispose();
			_acceptedCameraFrame = null;
			_acceptedTextureFrame?.PreviewRead.Publish(0uL);
			_acceptedTextureFrame?.Dispose();
			_acceptedTextureFrame = null;
			if (num)
			{
				Interlocked.Exchange(ref _renderFrameBusy, 0);
			}
		}
		_renderFrameReady.Set();
		Thread? renderThread = _renderThread;
		bool stopped = true;
		if (renderThread != null && renderThread != Thread.CurrentThread)
		{
			stopped = renderThread.Join(RenderWorkerStopTimeout);
		}
		if (stopped)
		{
			_renderThread = null;
			_renderFrameReady.Dispose();
		}
		return stopped;
	}

	private void CancelAcceptedRenderHandoff()
	{
		lock (_renderWorkerLock)
		{
			_acceptedCameraFrame?.Dispose();
			_acceptedCameraFrame = null;
			_acceptedTextureFrame?.PreviewRead.Publish(0uL);
			_acceptedTextureFrame?.Dispose();
			_acceptedTextureFrame = null;
		}
	}

	public bool WaitForTextureFrameRead(long frameNumber)
	{
		if (!_texturePreviewReads.TryRemove(
			frameNumber,
			out TexturePreviewRead? previewRead))
		{
			return true;
		}
		bool submitted = false;
		try
		{
			if (!previewRead.TryWaitForSubmission(
				TextureSubmissionTimeout,
				out ulong fenceValue))
			{
				previewRead.Discard();
				return false;
			}
			submitted = true;
			if (fenceValue == 0uL)
			{
				return true;
			}
			Direct3D12SwapChainRenderer? renderer =
				Volatile.Read(ref _renderer);
			return renderer == null
				|| renderer.WaitForFence(
					fenceValue,
					GpuOperationTimeout);
		}
		finally
		{
			if (submitted)
			{
				previewRead.Dispose();
			}
		}
	}

	public void DiscardTextureFrameRead(long frameNumber)
	{
		if (_texturePreviewReads.TryRemove(
			frameNumber,
			out TexturePreviewRead? previewRead))
		{
			previewRead.Discard();
		}
	}

	private void RecordSubmittedFrame()
	{
		Interlocked.Increment(ref _submittedFrames);
	}

	private bool ShouldAcceptRenderFrame()
	{
		lock (_renderThrottleLock)
		{
			if (_maxRenderFramesPerSecond <= 0.0)
			{
				return true;
			}
			long timestamp = Stopwatch.GetTimestamp();
			long minimumInterval = Math.Max(1L, (long)(Stopwatch.Frequency / _maxRenderFramesPerSecond));
			if (timestamp - _lastAcceptedRenderFrameTimestamp < minimumInterval)
			{
				return false;
			}
			_lastAcceptedRenderFrameTimestamp = timestamp;
			return true;
		}
	}

	private void RecordDroppedFrame()
	{
		Interlocked.Increment(ref _droppedFrames);
	}

	private void RecordRenderedFrame(string previewPath, string frameFormat, int width, int height, double sourceFramesPerSecond, bool denoiseEnabled, double denoiseStrength, VideoFrameColorSettings colorSettings, string recordingMode, string? fallbackReason, long frameNumber)
	{
		long num = Interlocked.Increment(ref _renderedFrames);
		long timestamp = Stopwatch.GetTimestamp();
		Volatile.Write(ref _lastRenderedFrameTimestamp, timestamp);
		long previousTimestamp = Volatile.Read(ref _lastDiagnosticsPublishedTimestamp);
		if (timestamp - previousTimestamp < Stopwatch.Frequency * 2L
			|| Interlocked.CompareExchange(ref _lastDiagnosticsPublishedTimestamp, timestamp, previousTimestamp) != previousTimestamp)
		{
			return;
		}
		long submittedFrames = Interlocked.Read(in _submittedFrames);
		long droppedFrames = Interlocked.Read(in _droppedFrames);
		DateTimeOffset utcNow = DateTimeOffset.UtcNow;
		Direct3D12PreviewDiagnostics e;
		lock (_diagnosticsLock)
		{
			double elapsedSeconds = Math.Max(
				0.001,
				(double)(timestamp - _diagnosticsFpsWindowStartTimestamp) / Stopwatch.Frequency);
			long num2 = Math.Max(0L, num - _diagnosticsFpsWindowStartRenderedFrames);
			_renderFramesPerSecond = num2 / elapsedSeconds;
			_diagnosticsFpsWindowStartTimestamp = timestamp;
			_diagnosticsFpsWindowStartRenderedFrames = num;
			e = (_diagnostics = new Direct3D12PreviewDiagnostics(previewPath, DeviceDescription, string.IsNullOrWhiteSpace(frameFormat) ? "unknown" : frameFormat, width, height, sourceFramesPerSecond, submittedFrames, num, droppedFrames, _renderFramesPerSecond, denoiseEnabled, denoiseStrength, colorSettings.HasVisibleAdjustments, recordingMode, fallbackReason, frameNumber, utcNow));
		}
		this.DiagnosticsChanged?.Invoke(this, e);
	}

	private static string FormatUploadFallbackPath(string path, string? directTextureFailureReason)
	{
		if (string.IsNullOrWhiteSpace(directTextureFailureReason))
		{
			return path;
		}
		string text = directTextureFailureReason.Trim();
		if (text.Length > 160)
		{
			text = text.Substring(0, 157) + "...";
		}
		return path + "; texture unavailable: " + text;
	}

	private static string? CombineTextureFailureReasons(string? directTextureFailureReason, string? sharedBridgeFailureReason)
	{
		string? text = (string.IsNullOrWhiteSpace(directTextureFailureReason) ? null : ("direct: " + directTextureFailureReason.Trim()));
		string? text2 = (string.IsNullOrWhiteSpace(sharedBridgeFailureReason) ? null : ("bridge: " + sharedBridgeFailureReason.Trim()));
		if (text == null)
		{
			if (text2 != null)
			{
				return text2;
			}
			return null;
		}
		if (text2 != null)
		{
			return text + "; " + text2;
		}
		return text;
	}

	private bool TryRenderNv12TextureUpload(Direct3D12SwapChainRenderer renderer, TextureNativeFrameLease frame, string? textureFailureReason, VideoFrameColorSettings colorSettings, bool denoiseEnabled, double denoiseStrength, PreviewOverlayStack overlays)
	{
		byte[]? nv12PreviewBytes = frame.Nv12PreviewBytes;
		if (nv12PreviewBytes == null || nv12PreviewBytes.Length <= 0 || frame.Nv12PreviewStride <= 0)
		{
			return false;
		}
		if (!renderer.RenderNv12Frame(nv12PreviewBytes, frame.Width, frame.Height, frame.Nv12PreviewStride, frame.FrameNumber, colorSettings, denoiseEnabled, denoiseStrength, overlays))
		{
			return false;
		}
		string text = FormatUploadFallbackPath("DX12 NV12 texture upload", textureFailureReason);
		ReportPreviewPath(text);
		RecordRenderedFrame(text, "NV12 texture upload", frame.Width, frame.Height, frame.FramesPerSecond, denoiseEnabled, denoiseStrength, colorSettings, RecordingMode, textureFailureReason, frame.FrameNumber);
		return true;
	}

	protected override void OnViewportCreated(nint hwnd, int width, int height)
	{
		nint num = Interlocked.Exchange(ref _nativeD3D12Device, IntPtr.Zero);
		Direct3D12SwapChainRenderer direct3D12SwapChainRenderer;
		try
		{
			lock (_rendererLock)
			{
				direct3D12SwapChainRenderer = (_renderer = new Direct3D12SwapChainRenderer(hwnd, width, height, num));
			}
			num = IntPtr.Zero;
		}
		finally
		{
			if (num != IntPtr.Zero)
			{
				Marshal.Release(num);
			}
		}
		this.StatusChanged?.Invoke(this, direct3D12SwapChainRenderer.DeviceDescription + " preview surface ready.");
	}

	protected override void OnViewportCreateFailed(Exception ex)
	{
		this.StatusChanged?.Invoke(this, "DX12 preview surface unavailable: " + ex.Message);
	}

	protected override void OnViewportDestroying()
	{
		TryDisposeRenderer("window destroy");
	}

	protected override void OnViewportResized(int width, int height)
	{
		lock (_rendererLock)
		{
			_renderer?.Resize(width, height);
		}
	}

	protected override void OnViewportResizeFailed(Exception ex)
	{
		this.StatusChanged?.Invoke(this, "DX12 preview resize failed: " + ex.Message);
	}

	public new void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			StopRenderWorker();
			nint num = Interlocked.Exchange(ref _nativeD3D12Device, IntPtr.Zero);
			if (num != IntPtr.Zero)
			{
				Marshal.Release(num);
			}
			base.Dispose();
		}
	}

	private void DisposeRenderer()
	{
		lock (_rendererLock)
		{
			DisposeRendererCore();
		}
	}

	private void TryDisposeRenderer(string context)
	{
		if (!Monitor.TryEnter(_rendererLock, RendererDisposeLockTimeout))
		{
			this.StatusChanged?.Invoke(this, "DX12 preview " + context + " deferred because the renderer is busy.");
			return;
		}
		try
		{
			DisposeRendererCore();
		}
		finally
		{
			Monitor.Exit(_rendererLock);
		}
	}

	private void DisposeRendererCore()
	{
		_renderer?.Dispose();
		_renderer = null;
	}
}
