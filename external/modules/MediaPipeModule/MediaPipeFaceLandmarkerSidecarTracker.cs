using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

public sealed class MediaPipeFaceLandmarkerSidecarTracker : IStatefulFaceLandmarkTracker, IFaceLandmarkTracker, IDisposable, IFaceLandmarkCropRefiner
{
	private static readonly BitmapSource EmptyBgraBitmap = CreateEmptyBgraBitmap();

	private readonly DenseFaceLandmarkModelInfo _modelInfo = DenseFaceLandmarkModelInfo.Load();

	private readonly Lazy<MediaPipeSidecarPythonEnvironment> _environment;

	private readonly bool _collectDiagnostics;

	private MediaPipeFaceLandmarkerSidecarClient? _client;

	private string _lastStatus = "MediaPipe sidecar not checked";

	public string Name => "MediaPipe Face Landmarker (official CPU Tasks)";

	public bool IsAvailable => _environment.Value.IsReady;

	public int MaxDetectionDimension { get; set; } = 1920;

	public MediaPipeFaceLandmarkerSidecarTracker(bool collectDiagnostics = false)
	{
		_collectDiagnostics = collectDiagnostics;
		_environment = new Lazy<MediaPipeSidecarPythonEnvironment>(delegate
		{
			MediaPipeSidecarPythonEnvironment mediaPipeSidecarPythonEnvironment = MediaPipeSidecarPythonEnvironment.Detect(_modelInfo);
			_lastStatus = mediaPipeSidecarPythonEnvironment.Status;
			return mediaPipeSidecarPythonEnvironment;
		});
	}

	public FaceLandmarkTrackingResult Detect(BitmapSource bitmap, DateTime capturedAtUtc)
	{
		if (!IsAvailable)
		{
			return new FaceLandmarkTrackingResult
			{
				BackendName = Name,
				BackendStatus = _lastStatus
			};
		}
		if (_client == null)
		{
			_client = new MediaPipeFaceLandmarkerSidecarClient(_environment.Value, _collectDiagnostics);
		}
		BitmapSource bitmap2 = ResizeForDetection(bitmap, MaxDetectionDimension);
		MediaPipeSidecarResponse mediaPipeSidecarResponse = _client.Analyze(bitmap2, capturedAtUtc, bitmap.PixelWidth, bitmap.PixelHeight);
		_lastStatus = (string.IsNullOrWhiteSpace(mediaPipeSidecarResponse.Status) ? _client.Status : mediaPipeSidecarResponse.Status);
		return MediaPipeFaceLandmarkerMapper.ToTrackingResult(
			mediaPipeSidecarResponse,
			capturedAtUtc,
			Name,
			bitmap2.PixelWidth,
			bitmap2.PixelHeight);
	}

	public FaceLandmarkTrackingResult DetectFaceCrop(BitmapSource bitmap, Rect normalizedFaceHint, DateTime capturedAtUtc)
	{
		if (!IsAvailable)
		{
			return new FaceLandmarkTrackingResult
			{
				BackendName = Name,
				BackendStatus = _lastStatus
			};
		}
		if (!TryCreateFaceCrop(bitmap, normalizedFaceHint, out BitmapSource cropBitmap, out Rect normalizedCrop))
		{
			return new FaceLandmarkTrackingResult
			{
				BackendName = Name,
				BackendStatus = "MediaPipe sidecar crop refinement skipped; face hint was outside the frame."
			};
		}
		if (_client == null)
		{
			_client = new MediaPipeFaceLandmarkerSidecarClient(_environment.Value, _collectDiagnostics);
		}
		BitmapSource bitmap2 = ResizeForDetection(cropBitmap, MaxDetectionDimension);
		MediaPipeSidecarResponse mediaPipeSidecarResponse = _client.Analyze(bitmap2, capturedAtUtc, cropBitmap.PixelWidth, cropBitmap.PixelHeight);
		_lastStatus = (string.IsNullOrWhiteSpace(mediaPipeSidecarResponse.Status) ? _client.Status : mediaPipeSidecarResponse.Status);
		return FaceLandmarkCropMapper.MapToFrame(
			MediaPipeFaceLandmarkerMapper.ToTrackingResult(
				mediaPipeSidecarResponse,
				capturedAtUtc,
				Name,
				bitmap2.PixelWidth,
				bitmap2.PixelHeight),
			normalizedCrop,
			"crop refined from face hint " + FormatCrop(normalizedCrop));
	}

	public void Reset()
	{
		_client?.Dispose();
		_client = null;
	}

	public void Dispose()
	{
		_client?.Dispose();
		_client = null;
	}

	private static BitmapSource ResizeForDetection(BitmapSource bitmap, int maximumDimension)
	{
		int num = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
		if (maximumDimension <= 0 || num <= maximumDimension)
		{
			return bitmap;
		}
		double num2 = (double)maximumDimension / (double)num;
		BitmapSource bitmapSource = new TransformedBitmap(bitmap, new ScaleTransform(num2, num2));
		if (bitmapSource.CanFreeze)
		{
			bitmapSource.Freeze();
		}
		return bitmapSource;
	}

	private static bool TryCreateFaceCrop(BitmapSource bitmap, Rect normalizedFaceHint, out BitmapSource cropBitmap, out Rect normalizedCrop)
	{
		cropBitmap = EmptyBgraBitmap;
		normalizedCrop = Rect.Empty;
		if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || normalizedFaceHint.Width <= 0.0 || normalizedFaceHint.Height <= 0.0)
		{
			return false;
		}
		Rect rect = ExpandAndClamp(normalizedFaceHint, 0.45, 0.6);
		int value = (int)Math.Floor(rect.Left * (double)bitmap.PixelWidth);
		int value2 = (int)Math.Floor(rect.Top * (double)bitmap.PixelHeight);
		int value3 = (int)Math.Ceiling(rect.Right * (double)bitmap.PixelWidth);
		int value4 = (int)Math.Ceiling(rect.Bottom * (double)bitmap.PixelHeight);
		value = Math.Clamp(value, 0, Math.Max(0, bitmap.PixelWidth - 1));
		value2 = Math.Clamp(value2, 0, Math.Max(0, bitmap.PixelHeight - 1));
		value3 = Math.Clamp(value3, value + 1, bitmap.PixelWidth);
		value4 = Math.Clamp(value4, value2 + 1, bitmap.PixelHeight);
		Int32Rect sourceRect = new Int32Rect(value, value2, value3 - value, value4 - value2);
		if (sourceRect.Width < 24 || sourceRect.Height < 24)
		{
			return false;
		}
		normalizedCrop = new Rect((double)sourceRect.X / (double)bitmap.PixelWidth, (double)sourceRect.Y / (double)bitmap.PixelHeight, (double)sourceRect.Width / (double)bitmap.PixelWidth, (double)sourceRect.Height / (double)bitmap.PixelHeight);
		BitmapSource bitmapSource = new CroppedBitmap(bitmap, sourceRect);
		int num = Math.Min(bitmapSource.PixelWidth, bitmapSource.PixelHeight);
		if (num > 0 && num < 320)
		{
			double num2 = Math.Min(4.0, 320.0 / (double)num);
			bitmapSource = new TransformedBitmap(bitmapSource, new ScaleTransform(num2, num2));
		}
		if (bitmapSource.CanFreeze)
		{
			bitmapSource.Freeze();
		}
		cropBitmap = bitmapSource;
		return true;
	}

	private static Rect ExpandAndClamp(Rect rect, double horizontalPadding, double verticalPadding)
	{
		double num = rect.Width * horizontalPadding;
		double num2 = rect.Height * verticalPadding;
		double num3 = Math.Clamp(rect.Left - num, 0.0, 1.0);
		double num4 = Math.Clamp(rect.Top - num2, 0.0, 1.0);
		double num5 = Math.Clamp(rect.Right + num, 0.0, 1.0);
		double num6 = Math.Clamp(rect.Bottom + num2, 0.0, 1.0);
		return new Rect(num3, num4, Math.Max(0.0, num5 - num3), Math.Max(0.0, num6 - num4));
	}

	private static string FormatCrop(Rect rect)
	{
		return $"{rect.Left:0.###},{rect.Top:0.###},{rect.Width:0.###}x{rect.Height:0.###}";
	}

	private static BitmapSource CreateEmptyBgraBitmap()
	{
		BitmapSource bitmap = BitmapSource.Create(1, 1, 96.0, 96.0, PixelFormats.Bgra32, null, new byte[4], 4);
		bitmap.Freeze();
		return bitmap;
	}
}
