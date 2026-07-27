using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Webcam.Common;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

public sealed class MediaFoundationBitmapCameraPreviewService : ICameraPreviewService, IDisposable
{
	private static readonly TimeSpan CaptureStopTimeout = TimeSpan.FromSeconds(3L);

	private MediaFoundationCameraDeviceFactory.MediaFoundationScope? _mediaFoundationScope;

	private IMFSourceReader? _reader;

	private object? _mediaSource;

	private CancellationTokenSource? _cancellation;

	private Task? _captureTask;

	private long _lastAnalysisFrameTimestamp;

	private int _activeWidth = 1280;

	private int _activeHeight = 720;

	private double _activeFramesPerSecond = 30.0;

	private Guid _activeSubtype = MediaFoundationGuids.MFVideoFormat_RGB32;

	private int _activeStride;

	private int _analysisFrameInFlight;

	public bool IsAvailable => OperatingSystem.IsWindows();

	public int MaxOutputWidth { get; set; } = 960;

	public double MaxOutputFramesPerSecond { get; set; } = 1000.0;

	public event EventHandler<BitmapSource>? FrameAvailable;

	public event EventHandler<CameraFrame>? CameraFrameAvailable;

	public event EventHandler<string>? StatusChanged;

	public Task<bool> StartAsync(CameraDevice camera, CameraVideoMode? mode, CancellationToken cancellationToken = default(CancellationToken))
	{
		Stop();
		if (!OperatingSystem.IsWindows())
		{
			NotifyStatusChanged("Media Foundation camera capture requires Windows.");
			return Task.FromResult(result: false);
		}
		return StartCoreAsync(camera, mode, cancellationToken);
	}

	public void Stop()
	{
		Task? captureTask = _captureTask;
		_cancellation?.Cancel();
		TryFlushSourceReader();
		try
		{
			captureTask?.Wait(CaptureStopTimeout);
		}
		catch
		{
		}
		_captureTask = null;
		ResetAnalysisFramePump();
		_cancellation?.Dispose();
		_cancellation = null;
		CleanupPreviewObjects();
	}

	public void Dispose()
	{
		Stop();
	}

	private async Task<bool> StartCoreAsync(CameraDevice camera, CameraVideoMode? mode, CancellationToken cancellationToken)
	{
		try
		{
			_cancellation = new CancellationTokenSource();
			TaskCompletionSource<string?> startup = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
			TaskCompletionSource<bool> firstFrame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			_captureTask = Task.Run(delegate
			{
				CaptureLoop(camera, mode, _cancellation.Token, startup, firstFrame);
			}, CancellationToken.None);
			if (await Task.WhenAny(startup.Task, Task.Delay(TimeSpan.FromSeconds(5L), cancellationToken)) != startup.Task)
			{
				Stop();
				NotifyStatusChanged("Could not start Media Foundation preview: timed out opening " + camera.Name + ".");
				return false;
			}
			string? text = await startup.Task;
			if (!string.IsNullOrWhiteSpace(text))
			{
				Stop();
				NotifyStatusChanged("Could not start Media Foundation preview: " + text);
				return false;
			}
			bool flag = await Task.WhenAny(firstFrame.Task, Task.Delay(TimeSpan.FromSeconds(2L), cancellationToken)) == firstFrame.Task;
			if (flag)
			{
				flag = await firstFrame.Task;
			}
			if (flag)
			{
				return true;
			}
			Stop();
			NotifyStatusChanged("Could not start Media Foundation preview: no frames arrived from " + camera.Name + ".");
			return false;
		}
		catch (OperationCanceledException)
		{
			Stop();
			return false;
		}
		catch (Exception ex2)
		{
			Stop();
			NotifyStatusChanged("Could not start Media Foundation preview: " + ex2.Message);
			return false;
		}
	}

	private void CaptureLoop(CameraDevice camera, CameraVideoMode? mode, CancellationToken cancellationToken, TaskCompletionSource<string?> startup, TaskCompletionSource<bool> firstFrame)
	{
		IMFSourceReader? iMFSourceReader = null;
		object? mediaSource = null;
		MediaFoundationCameraDeviceFactory.MediaFoundationScope? mediaFoundationScope = null;
		try
		{
			mediaFoundationScope = MediaFoundationCameraDeviceFactory.Startup();
			iMFSourceReader = MediaFoundationCameraDeviceFactory.CreatePreviewReader(camera, mode, out mediaSource);
			_mediaFoundationScope = mediaFoundationScope;
			_reader = iMFSourceReader;
			_mediaSource = mediaSource;
			UpdateActiveFormat(iMFSourceReader, mode);
			NotifyStatusChanged($"Media Foundation preview format: {_activeWidth}x{_activeHeight}@{_activeFramesPerSecond:0.###} {MediaFoundationInterop.FormatSubtype(_activeSubtype)}.");
			startup.TrySetResult(null);
			long analysisIntervalTicks = Math.Max(1L, (long)(Stopwatch.Frequency / Math.Clamp(MaxOutputFramesPerSecond, 1.0, 1000.0)));
			while (!cancellationToken.IsCancellationRequested)
			{
				int actualStreamIndex;
				int streamFlags;
				long timestamp;
				object? sample;
				int num = iMFSourceReader.ReadSample(-4, 0, out actualStreamIndex, out streamFlags, out timestamp, out sample);
				if (MediaFoundationInterop.Failed(num))
				{
					NotifyStatusChanged($"Camera read failed: 0x{num:X8}");
					Thread.Sleep(50);
					continue;
				}
				if ((streamFlags & 2) != 0)
				{
					NotifyStatusChanged("Camera preview ended.");
					break;
				}
				if (!(sample is IMFSample sample2))
				{
					MediaFoundationInterop.ReleaseComObject(sample);
					continue;
				}
				try
				{
					if (TryReadFrame(sample2, _activeWidth, _activeHeight, _activeSubtype, _activeStride, out CameraFrame frame))
					{
						using (frame)
						{
							firstFrame.TrySetResult(result: true);
							NotifyCameraFrameAvailable(frame);
							StartAnalysisFrameIfIdle(frame, analysisIntervalTicks);
						}
					}
				}
				finally
				{
					MediaFoundationInterop.ReleaseComObject(sample);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			startup.TrySetResult(ex2.Message);
			firstFrame.TrySetResult(result: false);
			NotifyStatusChanged(ex2.Message);
		}
		finally
		{
			MediaFoundationInterop.ReleaseComObject(iMFSourceReader);
			MediaFoundationInterop.ReleaseComObject(mediaSource);
			mediaFoundationScope?.Dispose();
			if (_reader == iMFSourceReader)
			{
				_reader = null;
			}
			if (_mediaSource == mediaSource)
			{
				_mediaSource = null;
			}
			if (_mediaFoundationScope == mediaFoundationScope)
			{
				_mediaFoundationScope = null;
			}
			startup.TrySetResult("Capture loop ended before startup completed.");
			firstFrame.TrySetResult(result: false);
		}
	}

	private void StartAnalysisFrameIfIdle(CameraFrame frame, long analysisIntervalTicks)
	{
		if (Volatile.Read(ref _analysisFrameInFlight) != 0)
		{
			return;
		}
		long timestamp = Stopwatch.GetTimestamp();
		if (timestamp - Volatile.Read(ref _lastAnalysisFrameTimestamp) < analysisIntervalTicks
			|| Interlocked.CompareExchange(ref _analysisFrameInFlight, 1, 0) != 0)
		{
			return;
		}
		Volatile.Write(ref _lastAnalysisFrameTimestamp, timestamp);
		CameraFrame ownedFrame;
		try
		{
			ownedFrame = frame.Duplicate();
		}
		catch
		{
			Interlocked.Exchange(ref _analysisFrameInFlight, 0);
			return;
		}
		Task.Run(delegate
		{
			ProcessAnalysisFrame(ownedFrame);
		});
	}

	private void ProcessAnalysisFrame(CameraFrame frame)
	{
		try
		{
			using (frame)
			{
				CancellationTokenSource? cancellation = _cancellation;
				if ((cancellation == null || !cancellation.IsCancellationRequested) && TryCreateBitmap(frame, out BitmapSource bitmap))
				{
					NotifyFrameAvailable(bitmap);
				}
			}
		}
		finally
		{
			Interlocked.Exchange(ref _analysisFrameInFlight, 0);
		}
	}

	private void ResetAnalysisFramePump()
	{
		Volatile.Write(ref _lastAnalysisFrameTimestamp, 0L);
	}

	private void NotifyCameraFrameAvailable(CameraFrame frame)
	{
		EventHandler<CameraFrame>? eventHandler = this.CameraFrameAvailable;
		if (eventHandler == null)
		{
			return;
		}
		Delegate[] invocationList = eventHandler.GetInvocationList();
		foreach (Delegate obj in invocationList)
		{
			try
			{
				((EventHandler<CameraFrame>)obj)(this, frame);
			}
			catch (Exception ex)
			{
				NotifyStatusChanged("Camera frame observer failed: " + ex.Message);
			}
		}
	}

	private void NotifyFrameAvailable(BitmapSource bitmap)
	{
		EventHandler<BitmapSource>? eventHandler = this.FrameAvailable;
		if (eventHandler == null)
		{
			return;
		}
		Delegate[] invocationList = eventHandler.GetInvocationList();
		foreach (Delegate obj in invocationList)
		{
			try
			{
				((EventHandler<BitmapSource>)obj)(this, bitmap);
			}
			catch (Exception ex)
			{
				NotifyStatusChanged("Preview frame observer failed: " + ex.Message);
			}
		}
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

	private void UpdateActiveFormat(IMFSourceReader reader, CameraVideoMode? requestedMode)
	{
		if (MediaFoundationInterop.Failed(reader.GetCurrentMediaType(-4, out IMFMediaType mediaType)))
		{
			_activeWidth = requestedMode?.Width ?? 1280;
			_activeHeight = requestedMode?.Height ?? 720;
			_activeFramesPerSecond = requestedMode?.FramesPerSecond ?? 30.0;
			_activeSubtype = MediaFoundationGuids.MFVideoFormat_RGB32;
			_activeStride = _activeWidth * 4;
			return;
		}
		try
		{
			if (MediaFoundationInterop.TryGetFrameSize(mediaType, out var width, out var height))
			{
				_activeWidth = width;
				_activeHeight = height;
			}
			if (MediaFoundationInterop.TryGetFrameRate(mediaType, out var framesPerSecond))
			{
				_activeFramesPerSecond = framesPerSecond;
			}
			if (!MediaFoundationInterop.Failed(mediaType.GetGUID(in MediaFoundationGuids.MF_MT_SUBTYPE, out var value)))
			{
				_activeSubtype = value;
			}
			if (!MediaFoundationInterop.Failed(mediaType.GetUINT32(in MediaFoundationGuids.MF_MT_DEFAULT_STRIDE, out var value2)))
			{
				_activeStride = value2;
			}
			else
			{
				_activeStride = ((_activeSubtype == MediaFoundationGuids.MFVideoFormat_RGB32) ? (_activeWidth * 4) : _activeWidth);
			}
		}
		finally
		{
			MediaFoundationInterop.ReleaseComObject(mediaType);
		}
	}

	private static bool TryReadFrame(IMFSample sample, int width, int height, Guid subtype, int stride, out CameraFrame frame)
	{
		frame = new CameraFrame(Array.Empty<byte>(), 0, 0, 0);
		IMFMediaBuffer? buffer = null;
		try
		{
			if (MediaFoundationInterop.Failed(sample.GetBufferByIndex(0, out buffer)))
			{
				MediaFoundationInterop.ThrowIfFailed(sample.ConvertToContiguousBuffer(out buffer));
			}
			MediaFoundationInterop.ThrowIfFailed(buffer.Lock(out var buffer2, out var _, out var currentLength));
			try
			{
				if (subtype == MediaFoundationGuids.MFVideoFormat_NV12)
				{
					int num = ((stride != 0) ? Math.Abs(stride) : width);
					int num2 = (height + 1) / 2;
					int num3 = num * height + num * num2;
					if (currentLength < num3)
					{
						return false;
					}
					CameraFrame rentedFrame = CameraFrame.RentNv12(width, height, num);
					try
					{
						Marshal.Copy(buffer2, rentedFrame.Nv12Bytes!, 0, num3);
						frame = rentedFrame;
					}
					catch
					{
						rentedFrame.Dispose();
						throw;
					}
					return true;
				}
				int num4 = ((stride != 0) ? Math.Abs(stride) : (width * 4));
				int num5 = num4 * height;
				if (currentLength < num5)
				{
					return false;
				}
				CameraFrame rentedBgraFrame = CameraFrame.RentBgra(width, height, num4);
				try
				{
					Marshal.Copy(buffer2, rentedBgraFrame.BgraBytes, 0, num5);
					frame = rentedBgraFrame;
				}
				catch
				{
					rentedBgraFrame.Dispose();
					throw;
				}
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

	private bool TryCreateBitmap(CameraFrame frame, out BitmapSource bitmap)
	{
		bitmap = null!;
		if (frame.Width <= 0 || frame.Height <= 0)
		{
			return false;
		}
		int num = Math.Clamp(MaxOutputWidth, 320, 3840);
		CameraFrame? convertedFrame = null;
		try
		{
			if (!frame.HasBgra)
			{
				if (!frame.HasNv12)
				{
					return false;
				}
				convertedFrame = CreateBgraFrameFromNv12(frame, num);
				frame = convertedFrame;
			}
			BitmapSource bitmapSource = BitmapSource.Create(frame.Width, frame.Height, 96.0, 96.0, PixelFormats.Bgra32, null, frame.BgraBytes, frame.Stride);
			bitmapSource.Freeze();
			if (frame.Width <= num)
			{
				bitmap = bitmapSource;
				return true;
			}
			double num2 = (double)num / frame.Width;
			TransformedBitmap transformedBitmap = new TransformedBitmap(bitmapSource, new ScaleTransform(num2, num2));
			transformedBitmap.Freeze();
			bitmap = transformedBitmap;
			return true;
		}
		finally
		{
			convertedFrame?.Dispose();
		}
	}

	private static CameraFrame CreateBgraFrameFromNv12(CameraFrame frame, int maximumWidth)
	{
		byte[]? nv12Bytes = frame.Nv12Bytes;
		if (nv12Bytes == null)
		{
			return frame;
		}
		int width = frame.Width;
		int height = frame.Height;
		int nv12Stride = frame.Nv12Stride;
		int num = width;
		int num2 = height;
		if (maximumWidth > 0 && width > maximumWidth)
		{
			double num3 = (double)maximumWidth / (double)width;
			num = maximumWidth;
			num2 = Math.Max(1, (int)Math.Round((double)height * num3));
		}
		int num4 = num * 4;
		CameraFrame convertedFrame = CameraFrame.RentBgra(num, num2, num4, "nv12-analysis");
		byte[] array = convertedFrame.BgraBytes;
		int num5 = nv12Stride * height;
		for (int i = 0; i < num2; i++)
		{
			int num6 = Math.Min(height - 1, (int)(((double)i + 0.5) * (double)height / (double)num2));
			int num7 = num6 * nv12Stride;
			int num8 = num5 + num6 / 2 * nv12Stride;
			int num9 = i * num4;
			for (int j = 0; j < num; j++)
			{
				int num10 = Math.Min(width - 1, (int)(((double)j + 0.5) * (double)width / (double)num));
				double num11 = (double)(nv12Bytes[num7 + num10] - 16) * 1.1643835616438356;
				int num12 = num8 + (num10 & -2);
				double num13 = (double)(int)nv12Bytes[num12] - 128.0;
				double num14 = (double)(int)nv12Bytes[num12 + 1] - 128.0;
				double value = num11 + 1.5748 * num14;
				double value2 = num11 - 0.1873 * num13 - 0.4681 * num14;
				double value3 = num11 + 1.8556 * num13;
				int num15 = num9 + j * 4;
				array[num15] = ClampByte(value3);
				array[num15 + 1] = ClampByte(value2);
				array[num15 + 2] = ClampByte(value);
				array[num15 + 3] = byte.MaxValue;
			}
		}
		return convertedFrame;
	}

	private static byte ClampByte(double value)
	{
		return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
	}

	private void CleanupPreviewObjects()
	{
		ResetPreviewState();
		MediaFoundationInterop.ReleaseComObject(_reader);
		MediaFoundationInterop.ReleaseComObject(_mediaSource);
		_reader = null;
		_mediaSource = null;
		_mediaFoundationScope?.Dispose();
		_mediaFoundationScope = null;
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

	private void ResetPreviewState()
	{
		_activeWidth = 1280;
		_activeHeight = 720;
		_activeFramesPerSecond = 30.0;
		_activeSubtype = MediaFoundationGuids.MFVideoFormat_RGB32;
		_activeStride = 0;
	}
}
