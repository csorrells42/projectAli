using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Infrastructure;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectX12;

namespace AvatarBuilder.Modules.Webcam.Ffmpeg;

public sealed class FfmpegCameraPreviewService : ICameraPreviewService, IDisposable
{
	private sealed record OutputLayout(int Width, int Height, double FramesPerSecond);

	private readonly string? _ffmpegPath = FfmpegLocator.FindFfmpeg();

	private readonly object _errorLock = new object();

	private readonly List<string> _recentErrors = new List<string>();

	private Process? _process;

	private CancellationTokenSource? _cancellation;

	private TaskCompletionSource<bool>? _firstFrameSignal;

	private Channel<CameraFrame>? _acceptedFrameHandoff;

	private Task? _frameReaderTask;

	private Task? _frameDeliveryTask;

	private Task? _errorReaderTask;

	private int _frameDeliveryBusy;

	private int _framesToQuarantine;

	private long _lastOverflowReportTimestamp;

	public bool IsAvailable => _ffmpegPath != null;

	public string? FfmpegPath => _ffmpegPath;

	public bool DenoiseEnabled { get; set; }

	public double DenoiseStrength { get; set; } = 2.0;

	public int MaxOutputWidth { get; set; } = 960;

	public double MaxOutputFramesPerSecond { get; set; } = 1000.0;

	public bool BitmapFramesEnabled { get; set; } = true;

	public event EventHandler<BitmapSource>? FrameAvailable;

	public event EventHandler<CameraFrame>? CameraFrameAvailable;

	public event EventHandler<string>? StatusChanged;

	public async Task<bool> StartAsync(CameraDevice camera, CameraVideoMode? mode, CancellationToken cancellationToken = default(CancellationToken))
	{
		Stop();
		ClearRecentErrors();
		if (_ffmpegPath == null)
		{
			this.StatusChanged?.Invoke(this, "FFmpeg was not found on this computer");
			return false;
		}
		string name = camera.Name;
		_cancellation = new CancellationTokenSource();
		_firstFrameSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		Interlocked.Exchange(ref _framesToQuarantine, 0);
		Interlocked.Exchange(ref _lastOverflowReportTimestamp, 0L);
		ProcessStartInfo processStartInfo = new ProcessStartInfo
		{
			FileName = _ffmpegPath,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		processStartInfo.ArgumentList.Add("-hide_banner");
		processStartInfo.ArgumentList.Add("-loglevel");
		processStartInfo.ArgumentList.Add("warning");
		processStartInfo.ArgumentList.Add("-fflags");
		processStartInfo.ArgumentList.Add("nobuffer");
		processStartInfo.ArgumentList.Add("-flags");
		processStartInfo.ArgumentList.Add("low_delay");
		processStartInfo.ArgumentList.Add("-rtbufsize");
		processStartInfo.ArgumentList.Add($"{GetRealtimeBufferMegabytes(mode)}M");
		if (mode != null && !mode.IsAuto)
		{
			if (!string.IsNullOrWhiteSpace(mode.InputFormat))
			{
				AddFormatArguments(processStartInfo, mode.InputFormat);
			}
			int? width = mode.Width;
			if (width.HasValue)
			{
				int valueOrDefault = width.GetValueOrDefault();
				width = mode.Height;
				if (width.HasValue)
				{
					int valueOrDefault2 = width.GetValueOrDefault();
					processStartInfo.ArgumentList.Add("-video_size");
					processStartInfo.ArgumentList.Add($"{valueOrDefault}x{valueOrDefault2}");
				}
			}
			double? framesPerSecond = mode.FramesPerSecond;
			if (framesPerSecond.HasValue)
			{
				double valueOrDefault3 = framesPerSecond.GetValueOrDefault();
				processStartInfo.ArgumentList.Add("-framerate");
				processStartInfo.ArgumentList.Add($"{valueOrDefault3:0.###}");
			}
		}
		processStartInfo.ArgumentList.Add("-f");
		processStartInfo.ArgumentList.Add("dshow");
		processStartInfo.ArgumentList.Add("-i");
		processStartInfo.ArgumentList.Add("video=" + name);
		OutputLayout outputLayout = GetOutputLayout(mode);
		processStartInfo.ArgumentList.Add("-vf");
		processStartInfo.ArgumentList.Add(CreatePreviewFilter(mode, outputLayout));
		processStartInfo.ArgumentList.Add("-an");
		processStartInfo.ArgumentList.Add("-sn");
		processStartInfo.ArgumentList.Add("-dn");
		processStartInfo.ArgumentList.Add("-pix_fmt");
		processStartInfo.ArgumentList.Add("nv12");
		processStartInfo.ArgumentList.Add("-f");
		processStartInfo.ArgumentList.Add("rawvideo");
		processStartInfo.ArgumentList.Add("pipe:1");
		try
		{
			LogCameraLine("Starting FFmpeg preview for " + name + " / " + (mode?.Label ?? "Auto"));
			LogCameraLine($"Preview fidelity: {outputLayout.Width}x{outputLayout.Height} NV12 at {outputLayout.FramesPerSecond:0.###} fps");
			LogCameraLine("Arguments: " + string.Join(" ", processStartInfo.ArgumentList));
			_process = Process.Start(processStartInfo);
		}
		catch (Exception ex)
		{
			LogCameraLine($"Could not start camera preview: {ex}");
			this.StatusChanged?.Invoke(this, "Could not start camera preview: " + ex.Message);
			return false;
		}
		if (_process == null)
		{
			this.StatusChanged?.Invoke(this, "Could not start camera preview");
			return false;
		}
		Channel<CameraFrame> acceptedFrameHandoff = (_acceptedFrameHandoff = Channel.CreateBounded<CameraFrame>(new BoundedChannelOptions(1)
		{
			SingleReader = false,
			SingleWriter = true,
			FullMode = BoundedChannelFullMode.Wait
		}));
		_frameDeliveryTask = DeliverFramesAsync(acceptedFrameHandoff.Reader, _cancellation.Token);
		_frameReaderTask = ReadFramesAsync(_process, outputLayout, acceptedFrameHandoff, _cancellation.Token);
		_errorReaderTask = ReadErrorsAsync(_process, _cancellation.Token);
		Task exitTask = WatchExitAsync(_process, _cancellation.Token);
		this.StatusChanged?.Invoke(this, "Starting preview: " + name);
		try
		{
			Task<bool> firstFrameTask = _firstFrameSignal.Task;
			Task readinessTimeout = Task.Delay(TimeSpan.FromSeconds(5L), cancellationToken);
			InlineArray3<Task> buffer = default(InlineArray3<Task>);
			buffer[0] = firstFrameTask;
			buffer[1] = exitTask;
			buffer[2] = readinessTimeout;
			Task task = await Task.WhenAny(buffer);
			if (task == firstFrameTask)
			{
				return true;
			}
			if (task == exitTask && _process.HasExited)
			{
				string recentErrorSummary = GetRecentErrorSummary();
				string text = (string.IsNullOrWhiteSpace(recentErrorSummary) ? $"Camera preview stopped with FFmpeg exit code {_process.ExitCode}" : $"Camera preview stopped with FFmpeg exit code {_process.ExitCode}: {recentErrorSummary}");
				LogCameraLine(text);
				this.StatusChanged?.Invoke(this, text);
				Stop();
				return false;
			}
			if (task == readinessTimeout)
			{
				LogCameraLine("Camera preview opened but did not deliver a frame within 5 seconds.");
				this.StatusChanged?.Invoke(this, "Camera preview opened but did not deliver a frame within 5 seconds.");
				Stop();
				return false;
			}
		}
		catch (OperationCanceledException)
		{
			Stop();
			return false;
		}
		return true;
	}

	private string CreatePreviewFilter(CameraVideoMode? mode, OutputLayout outputLayout)
	{
		List<string> list = new List<string>();
		IFormatProvider invariantCulture = CultureInfo.InvariantCulture;
		IFormatProvider provider = invariantCulture;
		DefaultInterpolatedStringHandler handler = new DefaultInterpolatedStringHandler(4, 1, invariantCulture);
		handler.AppendLiteral("fps=");
		handler.AppendFormatted(outputLayout.FramesPerSecond, "0.###");
		list.Add(string.Create(provider, ref handler));
		List<string> list2 = list;
		if (outputLayout.Width != (mode?.Width ?? 1280) || outputLayout.Height != (mode?.Height ?? 720))
		{
			list2.Add($"scale={outputLayout.Width}:{outputLayout.Height}");
		}
		if (DenoiseEnabled)
		{
			double num = Math.Clamp(DenoiseStrength, 0.5, 8.0);
			double num2 = Math.Max(0.25, num * 0.7);
			invariantCulture = CultureInfo.InvariantCulture;
			IFormatProvider provider2 = invariantCulture;
			DefaultInterpolatedStringHandler handler2 = new DefaultInterpolatedStringHandler(10, 4, invariantCulture);
			handler2.AppendLiteral("hqdn3d=");
			handler2.AppendFormatted(num, "0.##");
			handler2.AppendLiteral(":");
			handler2.AppendFormatted(num2, "0.##");
			handler2.AppendLiteral(":");
			handler2.AppendFormatted(num * 1.5, "0.##");
			handler2.AppendLiteral(":");
			handler2.AppendFormatted(num2 * 1.5, "0.##");
			list2.Add(string.Create(provider2, ref handler2));
		}
		return string.Join(",", list2);
	}

	private OutputLayout GetOutputLayout(CameraVideoMode? mode)
	{
		int num = mode?.Width ?? 1280;
		int num2 = mode?.Height ?? 720;
		double framesPerSecond = Math.Clamp(Math.Min(mode?.FramesPerSecond ?? 30.0, MaxOutputFramesPerSecond), 1.0, 1000.0);
		int val = Math.Clamp(MaxOutputWidth, 320, 3840);
		int num3 = Math.Min(num, val);
		double num4 = (double)num3 / (double)Math.Max(1, num);
		int height = Math.Max(2, (int)Math.Round((double)num2 * num4 / 2.0) * 2);
		return new OutputLayout(num3, height, framesPerSecond);
	}

	private int GetRealtimeBufferMegabytes(CameraVideoMode? mode)
	{
		int num = mode?.Width ?? MaxOutputWidth;
		int num2 = mode?.Height ?? 720;
		int num3 = num * num2;
		int num4 = Math.Min(num, Math.Clamp(MaxOutputWidth, 320, 3840)) * Math.Min(num2, 2160);
		if (num3 >= 8294400 || num4 >= 8294400)
		{
			return 256;
		}
		if (num3 >= 2073600 || num4 >= 2073600)
		{
			return 96;
		}
		return 32;
	}

	private static void AddFormatArguments(ProcessStartInfo startInfo, string format)
	{
		format = NormalizeInputFormat(format);
		if (IsPixelFormat(format))
		{
			startInfo.ArgumentList.Add("-pixel_format");
			startInfo.ArgumentList.Add(format);
		}
		else
		{
			startInfo.ArgumentList.Add("-vcodec");
			startInfo.ArgumentList.Add(format);
		}
	}

	private static string NormalizeInputFormat(string format)
	{
		return format.ToLowerInvariant() switch
		{
			"mjpg" => "mjpeg", 
			"yuy2" => "yuyv422", 
			"uyvy" => "uyvy422", 
			_ => format, 
		};
	}

	private static bool IsPixelFormat(string format)
	{
		if (!format.Equals("yuyv422", StringComparison.OrdinalIgnoreCase) && !format.Equals("uyvy422", StringComparison.OrdinalIgnoreCase) && !format.Equals("nv12", StringComparison.OrdinalIgnoreCase) && !format.Equals("rgb32", StringComparison.OrdinalIgnoreCase) && !format.Equals("rgb24", StringComparison.OrdinalIgnoreCase))
		{
			return format.Equals("bgr24", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	public void Stop()
	{
		CancellationTokenSource? cancellationTokenSource = Interlocked.Exchange(ref _cancellation, null);
		cancellationTokenSource?.Cancel();
		Interlocked.Exchange(ref _acceptedFrameHandoff, null)?.Writer.TryComplete();
		_firstFrameSignal = null;
		Interlocked.Exchange(ref _framesToQuarantine, 0);
		Process? process = Interlocked.Exchange(ref _process, null);
		try
		{
			if (process != null && !process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				process.WaitForExit(1500);
			}
		}
		catch
		{
		}
		finally
		{
			process?.Dispose();
		}
		Task? task = Interlocked.Exchange(ref _frameReaderTask, null);
		Task? task2 = Interlocked.Exchange(ref _frameDeliveryTask, null);
		Task? task3 = Interlocked.Exchange(ref _errorReaderTask, null);
		try
		{
			Task[] array = new Task?[] { task, task2, task3 }.OfType<Task>().ToArray();
			if (array.Length != 0)
			{
				Task.WaitAll(array, TimeSpan.FromMilliseconds(750L));
			}
		}
		catch
		{
		}
		finally
		{
			cancellationTokenSource?.Dispose();
		}
	}

	public void Dispose()
	{
		Stop();
	}

	private async Task ReadErrorsAsync(Process process, CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				string? text = await process.StandardError.ReadLineAsync(cancellationToken);
				if (text == null)
				{
					break;
				}
				if (string.IsNullOrWhiteSpace(text))
				{
					continue;
				}
				if (IsRealtimeBufferOverflow(text))
				{
					QuarantineFramesAfterOverflow();
					ReportRealtimeBufferOverflow();
					continue;
				}
				AddRecentError(text);
				string? text2 = SimplifyStatusLine(text);
				if (text2 != null)
				{
					LogCameraLine(text2);
					this.StatusChanged?.Invoke(this, text2);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			this.StatusChanged?.Invoke(this, ex2.Message);
		}
	}

	private async Task WatchExitAsync(Process process, CancellationToken cancellationToken)
	{
		try
		{
			await process.WaitForExitAsync(cancellationToken);
			if (!cancellationToken.IsCancellationRequested && process.ExitCode != 0)
			{
				string recentErrorSummary = GetRecentErrorSummary();
				string text = (string.IsNullOrWhiteSpace(recentErrorSummary) ? $"Camera preview stopped with FFmpeg exit code {process.ExitCode}" : $"Camera preview stopped with FFmpeg exit code {process.ExitCode}: {recentErrorSummary}");
				LogCameraLine(text);
				this.StatusChanged?.Invoke(this, text);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			this.StatusChanged?.Invoke(this, ex2.Message);
		}
	}

	private static string? SimplifyStatusLine(string line)
	{
		if (line.Contains("[INFO]", StringComparison.OrdinalIgnoreCase) || line.Contains("Failed to open settings hive", StringComparison.OrdinalIgnoreCase) || line.Contains("Failed to open NBX hive", StringComparison.OrdinalIgnoreCase) || line.Contains("Creating WndMsg Listener Window", StringComparison.OrdinalIgnoreCase) || line.Contains("Destroying WndMsg Listener Window", StringComparison.OrdinalIgnoreCase) || line.Contains("Unregistered window class", StringComparison.OrdinalIgnoreCase) || line.Contains("deprecated pixel format", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		return line;
	}

	private async Task ReadFramesAsync(Process process, OutputLayout outputLayout, Channel<CameraFrame> acceptedFrameHandoff, CancellationToken cancellationToken)
	{
		Stream stream = process.StandardOutput.BaseStream;
		int frameByteCount = checked(outputLayout.Width * outputLayout.Height * 3) / 2;
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				CameraFrame frame = CameraFrame.RentNv12(outputLayout.Width, outputLayout.Height, outputLayout.Width, "nv12-ffmpeg");
				CameraFrame? ownedFrame = frame;
				try
				{
					byte[]? nv12Bytes = frame.Nv12Bytes;
					if (nv12Bytes is null || !(await ReadExactFrameAsync(stream, nv12Bytes, frameByteCount, cancellationToken)))
					{
						break;
					}
					if (!ConsumeQuarantinedFrame())
					{
						_firstFrameSignal?.TrySetResult(result: true);
						if (TryAcceptFrameForDelivery(acceptedFrameHandoff, frame))
						{
							ownedFrame = null;
						}
					}
				}
				finally
				{
					ownedFrame?.Dispose();
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			this.StatusChanged?.Invoke(this, ex2.Message);
		}
		finally
		{
			acceptedFrameHandoff.Writer.TryComplete();
		}
	}

	private async Task DeliverFramesAsync(ChannelReader<CameraFrame> reader, CancellationToken cancellationToken)
	{
		try
		{
			while (await reader.WaitToReadAsync(cancellationToken))
			{
				if (!reader.TryRead(out CameraFrame? item))
				{
					continue;
				}
				try
				{
					using (item)
					{
						this.CameraFrameAvailable?.Invoke(this, item);
						if (BitmapFramesEnabled && this.FrameAvailable != null)
						{
							BitmapSource? bitmapSource = CreateBitmap(item);
							if (bitmapSource != null)
							{
								this.FrameAvailable(this, bitmapSource);
							}
						}
					}
				}
				finally
				{
					Interlocked.Exchange(ref _frameDeliveryBusy, 0);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			this.StatusChanged?.Invoke(this, "Camera frame delivery paused: " + ex2.Message);
		}
		finally
		{
			while (reader.TryRead(out CameraFrame? pendingFrame))
			{
				pendingFrame?.Dispose();
			}
			Interlocked.Exchange(ref _frameDeliveryBusy, 0);
		}
	}

	private bool TryAcceptFrameForDelivery(Channel<CameraFrame> acceptedFrameHandoff, CameraFrame frame)
	{
		if (Interlocked.CompareExchange(ref _frameDeliveryBusy, 1, 0) != 0)
		{
			return false;
		}
		if (acceptedFrameHandoff.Writer.TryWrite(frame))
		{
			return true;
		}
		Interlocked.Exchange(ref _frameDeliveryBusy, 0);
		return false;
	}

	private static bool IsRealtimeBufferOverflow(string line)
	{
		if (line.Contains("real-time buffer", StringComparison.OrdinalIgnoreCase))
		{
			return line.Contains("too full", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private void QuarantineFramesAfterOverflow()
	{
		int num;
		do
		{
			num = Volatile.Read(in _framesToQuarantine);
		}
		while (num < 8 && Interlocked.CompareExchange(ref _framesToQuarantine, 8, num) != num);
	}

	private bool ConsumeQuarantinedFrame()
	{
		int num;
		do
		{
			num = Volatile.Read(in _framesToQuarantine);
			if (num <= 0)
			{
				return false;
			}
		}
		while (Interlocked.CompareExchange(ref _framesToQuarantine, num - 1, num) != num);
		return true;
	}

	private void ReportRealtimeBufferOverflow()
	{
		long timestamp = Stopwatch.GetTimestamp();
		long num = Volatile.Read(in _lastOverflowReportTimestamp);
		if ((num == 0L || !(Stopwatch.GetElapsedTime(num, timestamp) < TimeSpan.FromSeconds(2L))) && Interlocked.CompareExchange(ref _lastOverflowReportTimestamp, timestamp, num) == num)
		{
			LogCameraLine("Camera input briefly fell behind; transition frames were quarantined before preview and calibration.");
			this.StatusChanged?.Invoke(this, "Camera input briefly fell behind; transition frames were quarantined before preview and calibration.");
		}
	}

	private static async Task<bool> ReadExactFrameAsync(Stream stream, byte[] buffer, int byteCount, CancellationToken cancellationToken)
	{
		int num;
		for (int offset = 0; offset < byteCount; offset += num)
		{
			num = await stream.ReadAsync(buffer.AsMemory(offset, byteCount - offset), cancellationToken);
			if (num == 0)
			{
				return false;
			}
		}
		return true;
	}

	private static BitmapSource? CreateBitmap(CameraFrame frame)
	{
		if (frame.HasNv12)
		{
		byte[]? nv12Bytes = frame.Nv12Bytes;
			if (nv12Bytes != null)
			{
				try
				{
					int outputWidth;
					int outputHeight;
					int bgraStride;
		byte[]? array = Nv12FrameConverter.ConvertToBgra(nv12Bytes, frame.Nv12Stride, frame.Width, frame.Height, frame.Width, out outputWidth, out outputHeight, out bgraStride);
					if (array == null || array.Length == 0)
					{
						return null;
					}
					BitmapSource bitmapSource = BitmapSource.Create(outputWidth, outputHeight, 96.0, 96.0, PixelFormats.Bgra32, null, array, bgraStride);
					bitmapSource.Freeze();
					return bitmapSource;
				}
				catch
				{
					return null;
				}
			}
		}
		return null;
	}

	private void ClearRecentErrors()
	{
		lock (_errorLock)
		{
			_recentErrors.Clear();
		}
	}

	private void AddRecentError(string line)
	{
		lock (_errorLock)
		{
			_recentErrors.Add(line);
			if (_recentErrors.Count > 12)
			{
				_recentErrors.RemoveAt(0);
			}
		}
	}

	private string GetRecentErrorSummary()
	{
		lock (_errorLock)
		{
			return string.Join(" | ", (from line in _recentErrors.Select(SimplifyStatusLine)
				where !string.IsNullOrWhiteSpace(line)
				select line).TakeLast(4));
		}
	}

	private static void LogCameraLine(string line)
	{
		try
		{
			File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "AvatarBuilder-camera.log"), $"{DateTime.Now:O} {line}{Environment.NewLine}");
		}
		catch
		{
		}
	}
}
