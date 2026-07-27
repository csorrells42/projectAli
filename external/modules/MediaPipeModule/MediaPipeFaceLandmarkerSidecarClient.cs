using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Vision.Diagnostics;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

internal sealed class MediaPipeFaceLandmarkerSidecarClient : IDisposable
{
	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

	private readonly MediaPipeSidecarPythonEnvironment _environment;

	private readonly bool _collectDiagnostics;

	private readonly MediaPipeSharedMemoryFrame _sharedMemoryFrame = new MediaPipeSharedMemoryFrame();

	private readonly MediaPipeSharedMemoryLandmarks _sharedMemoryLandmarks = new MediaPipeSharedMemoryLandmarks();

	private readonly object _sync = new object();

	private readonly TimeSpan _timeout;

	private Process? _process;

	private bool _firstResponseAfterStart;

	private int _requestNumber;

	private long _lastTimestampMilliseconds;

	private MediaPipeSharedMemoryFrameDescriptor _lastSentFrameDescriptor;

	private int _disposed;

	private int _analysisInFlight;

	public string Status { get; private set; } = "";

	public MediaPipeFaceLandmarkerSidecarClient(MediaPipeSidecarPythonEnvironment environment, bool collectDiagnostics)
	{
		_environment = environment;
		_collectDiagnostics = collectDiagnostics;
		_timeout = TimeSpan.FromMilliseconds(ReadTimeoutMilliseconds());
	}

	public MediaPipeSidecarResponse Analyze(BitmapSource bitmap, DateTime capturedAtUtc, int sourceWidth = 0, int sourceHeight = 0)
	{
		if (Interlocked.CompareExchange(ref _analysisInFlight, 1, 0) != 0)
		{
			return Error("MediaPipe frame dropped before conversion because the selected processor is busy.");
		}
		try
		{
			lock (_sync)
			{
				if (Volatile.Read(in _disposed) != 0)
				{
					return Error("MediaPipe sidecar client is stopped.");
				}
				if (!_environment.IsReady)
				{
					return Error(_environment.Status);
				}
				if (!EnsureProcess())
				{
					return Error(Status);
				}
				try
				{
					long totalStartedAt = _collectDiagnostics ? Stopwatch.GetTimestamp() : 0L;
					long prepareStartedAt = totalStartedAt;
					MediaPipeSharedMemoryFrameDescriptor mediaPipeSharedMemoryFrameDescriptor = _sharedMemoryFrame.Write(bitmap);
					long timestampMilliseconds = (_lastTimestampMilliseconds = Math.Max(Environment.TickCount64, _lastTimestampMilliseconds + 1));
					int requestId = Interlocked.Increment(ref _requestNumber);
					bool sendTransportSetup = _firstResponseAfterStart || mediaPipeSharedMemoryFrameDescriptor != _lastSentFrameDescriptor;
					string value;
					if (sendTransportSetup || _collectDiagnostics)
					{
						MediaPipeSidecarRequest mediaPipeSidecarRequest = new MediaPipeSidecarRequest
						{
							RequestId = requestId,
							TimestampMilliseconds = timestampMilliseconds,
							SharedMemoryName = sendTransportSetup ? mediaPipeSharedMemoryFrameDescriptor.Name : null,
							SharedMemoryCapacityBytes = sendTransportSetup ? mediaPipeSharedMemoryFrameDescriptor.CapacityBytes : 0,
							ImageByteLength = sendTransportSetup ? mediaPipeSharedMemoryFrameDescriptor.ImageByteLength : 0,
							ImageWidth = sendTransportSetup ? mediaPipeSharedMemoryFrameDescriptor.Width : 0,
							ImageHeight = sendTransportSetup ? mediaPipeSharedMemoryFrameDescriptor.Height : 0,
							ImageStride = sendTransportSetup ? mediaPipeSharedMemoryFrameDescriptor.Stride : 0,
							ImagePixelFormat = sendTransportSetup ? mediaPipeSharedMemoryFrameDescriptor.PixelFormat : null,
							LandmarkSharedMemoryName = sendTransportSetup ? _sharedMemoryLandmarks.Name : null,
							LandmarkSharedMemoryCapacityBytes = sendTransportSetup ? _sharedMemoryLandmarks.Capacity : 0,
							CollectDiagnostics = _collectDiagnostics
						};
						value = JsonSerializer.Serialize(mediaPipeSidecarRequest, JsonOptions);
					}
					else
					{
						value = string.Create(
							CultureInfo.InvariantCulture,
							$"{{\"requestId\":{requestId},\"timestampMilliseconds\":{timestampMilliseconds}}}");
					}
					double prepareMilliseconds = ElapsedMilliseconds(prepareStartedAt);
					long roundTripStartedAt = _collectDiagnostics ? Stopwatch.GetTimestamp() : 0L;
					Process process = _process ?? throw new InvalidOperationException("MediaPipe sidecar process is unavailable.");
					process.StandardInput.WriteLine(value);
					process.StandardInput.Flush();
					Task<string?> task = process.StandardOutput.ReadLineAsync();
					TimeSpan timeout = (_firstResponseAfterStart ? TimeSpan.FromMilliseconds(ReadStartupTimeoutMilliseconds()) : _timeout);
					if (!task.Wait(timeout))
					{
						RestartAfterFailure("MediaPipe sidecar timed out waiting for a frame response.");
						return Error(Status);
					}
					_firstResponseAfterStart = false;
					string? result = task.Result;
					double roundTripMilliseconds = ElapsedMilliseconds(roundTripStartedAt);
					if (string.IsNullOrWhiteSpace(result))
					{
						RestartAfterFailure("MediaPipe sidecar closed its output stream.");
						return Error(Status);
					}
					long parseStartedAt = _collectDiagnostics ? Stopwatch.GetTimestamp() : 0L;
					MediaPipeSidecarResponse? mediaPipeSidecarResponse = JsonSerializer.Deserialize<MediaPipeSidecarResponse>(result, JsonOptions);
					double parseMilliseconds = ElapsedMilliseconds(parseStartedAt);
					if (mediaPipeSidecarResponse == null)
					{
						return Error("MediaPipe sidecar returned an empty response.");
					}
					if (mediaPipeSidecarResponse.RequestId != requestId)
					{
						return Error($"MediaPipe sidecar response id mismatch: expected {requestId}, got {mediaPipeSidecarResponse.RequestId}.");
					}
					if (sendTransportSetup && mediaPipeSidecarResponse.Ok)
					{
						_lastSentFrameDescriptor = mediaPipeSharedMemoryFrameDescriptor;
					}
					if (mediaPipeSidecarResponse.HasFace)
					{
						_sharedMemoryLandmarks.Read(
							mediaPipeSidecarResponse.LandmarkCount,
							mediaPipeSidecarResponse.FacialTransformationMatrixCount,
							out MediaPipeSidecarLandmark[] landmarks,
							out double[] transformationMatrix);
						mediaPipeSidecarResponse.Landmarks = landmarks;
						mediaPipeSidecarResponse.FacialTransformationMatrix = transformationMatrix;
					}
					if (mediaPipeSidecarResponse.Status.Length == 0)
					{
						mediaPipeSidecarResponse.Status = GetNormalStatus(mediaPipeSidecarResponse.HasFace);
					}
					Status = mediaPipeSidecarResponse.Status;
					if (_collectDiagnostics)
					{
						mediaPipeSidecarResponse.Diagnostics = new VisionPipelineDiagnostics
						{
							CapturedAtUtc = capturedAtUtc,
							Backend = "MediaPipe Face Landmarker",
							Mode = "video-tracking-shared-memory-cpu",
							SourceWidth = ((sourceWidth > 0) ? sourceWidth : bitmap.PixelWidth),
							SourceHeight = ((sourceHeight > 0) ? sourceHeight : bitmap.PixelHeight),
							InputWidth = bitmap.PixelWidth,
							InputHeight = bitmap.PixelHeight,
							EncodedPayloadBytes = mediaPipeSharedMemoryFrameDescriptor.ImageByteLength,
							HasFace = mediaPipeSidecarResponse.HasFace,
							ClientPrepareMilliseconds = prepareMilliseconds,
							SidecarRoundTripMilliseconds = roundTripMilliseconds,
							ClientParseMilliseconds = parseMilliseconds,
							EndToEndMilliseconds = ElapsedMilliseconds(totalStartedAt),
							SidecarStagesMilliseconds = mediaPipeSidecarResponse.TimingsMilliseconds,
							Status = mediaPipeSidecarResponse.Status
						};
					}
					return mediaPipeSidecarResponse;
				}
				catch (Exception ex)
				{
					RestartAfterFailure("MediaPipe sidecar call failed: " + ex.Message);
					return Error(Status);
				}
			}
		}
		finally
		{
			Volatile.Write(ref _analysisInFlight, 0);
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
		{
			StopProcess();
			_sharedMemoryFrame.Dispose();
			_sharedMemoryLandmarks.Dispose();
		}
	}

	private bool EnsureProcess()
	{
		if (Volatile.Read(in _disposed) != 0)
		{
			Status = "MediaPipe sidecar client is stopped.";
			return false;
		}
		Process? process = _process;
		if (process != null && !process.HasExited)
		{
			return true;
		}
		StopProcess();
		Process? process2 = null;
		try
		{
			process2 = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = _environment.PythonPath,
					UseShellExecute = false,
					RedirectStandardInput = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true
				}
			};
			_environment.ConfigureStartInfo(process2.StartInfo, probe: false);
			if (!process2.Start())
			{
				Status = "MediaPipe sidecar process did not start.";
				return false;
			}
			_process = process2;
			Process runningProcess = process2;
			process2 = null;
			_firstResponseAfterStart = true;
			_lastSentFrameDescriptor = default;
			_lastTimestampMilliseconds = 0L;
			Task.Run(delegate
			{
				ReadErrors(runningProcess);
			});
			Status = "MediaPipe official CPU sidecar process started.";
			return true;
		}
		catch (Exception ex)
		{
			Status = "MediaPipe sidecar process failed to start: " + ex.Message;
			return false;
		}
		finally
		{
			process2?.Dispose();
		}
	}

	private void RestartAfterFailure(string status)
	{
		Status = status;
		StopProcess();
	}

	private void StopProcess()
	{
		Process? process = Interlocked.Exchange(ref _process, null);
		_lastSentFrameDescriptor = default;
		if (process == null)
		{
			return;
		}
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch
		{
		}
		finally
		{
			process.Dispose();
		}
	}

	private void ReadErrors(Process process)
	{
		try
		{
			while (!process.HasExited)
			{
				string? text = process.StandardError.ReadLine();
				if (text != null)
				{
					if (!string.IsNullOrWhiteSpace(text))
					{
						Status = text.Trim();
					}
					continue;
				}
				break;
			}
		}
		catch
		{
		}
	}

	private static MediaPipeSidecarResponse Error(string status)
	{
		return new MediaPipeSidecarResponse
		{
			Ok = false,
			Status = status
		};
	}

	private static double ElapsedMilliseconds(long startedAt)
	{
		return startedAt == 0L ? 0.0 : Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
	}

	private string GetNormalStatus(bool hasFace)
	{
		return hasFace ? "MediaPipe dense landmark lock" : "MediaPipe sidecar searching";
	}

	private static int ReadTimeoutMilliseconds()
	{
		if (!int.TryParse(Environment.GetEnvironmentVariable("AVATAR_BUILDER_MEDIAPIPE_TIMEOUT_MS"), out var result))
		{
			return 1800;
		}
		return Math.Clamp(result, 250, 10000);
	}

	private static int ReadStartupTimeoutMilliseconds()
	{
		if (!int.TryParse(Environment.GetEnvironmentVariable("AVATAR_BUILDER_MEDIAPIPE_STARTUP_TIMEOUT_MS"), out var result))
		{
			return 10000;
		}
		return Math.Clamp(result, 1000, 30000);
	}
}
