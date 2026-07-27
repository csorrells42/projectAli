using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed class OpenCvYuNetFaceDetector : IDisposable
{
	private readonly object _detectorLock = new();

	private readonly OpenCvYuNetModelInfo _modelInfo = OpenCvYuNetModelInfo.Load();

	private FaceDetectorYN? _detector;

	private Size _inputSize;

	private string _initializationStatus = "";

	public bool IsAvailable
	{
		get
		{
			lock (_detectorLock)
			{
				if (_modelInfo.IsReady)
				{
					return _detector is not null
						|| EnsureDetector(new Size(320, 320));
				}
				return false;
			}
		}
	}

	public string Status
	{
		get
		{
			lock (_detectorLock)
			{
				if (!_modelInfo.IsReady)
				{
					return _modelInfo.Status;
				}
				if (!string.IsNullOrWhiteSpace(_initializationStatus))
				{
					return _initializationStatus;
				}
				return "OpenCV YuNet face detector waiting";
			}
		}
	}

	public YuNetFaceDetection? Detect(Mat image)
	{
		return (from face in DetectAll(image)
			orderby face.Score descending
			select face).FirstOrDefault();
	}

	public IReadOnlyList<YuNetFaceDetection> DetectAll(Mat image)
	{
		lock (_detectorLock)
		{
			if (!_modelInfo.IsReady || image.Empty())
			{
				return Array.Empty<YuNetFaceDetection>();
			}
			Size inputSize = new Size(image.Width, image.Height);
			if (!EnsureDetector(inputSize))
			{
				return Array.Empty<YuNetFaceDetection>();
			}
			using Mat converted = new();
			Mat bgr = image;
			if (image.Channels() == 1)
			{
				Cv2.CvtColor(image, converted, ColorConversionCodes.GRAY2BGR);
				bgr = converted;
			}
			else if (image.Channels() == 4)
			{
				Cv2.CvtColor(image, converted, ColorConversionCodes.BGRA2BGR);
				bgr = converted;
			}
			else if (image.Channels() != 3)
			{
				return Array.Empty<YuNetFaceDetection>();
			}
			using Mat faces = new Mat();
			FaceDetectorYN detector = _detector
				?? throw new InvalidOperationException(
					"YuNet initialization reported success without a detector instance.");
			detector.Detect(bgr, faces);
			return ParseFaces(faces, image.Width, image.Height);
		}
	}

	public void Dispose()
	{
		lock (_detectorLock)
		{
			_detector?.Dispose();
			_detector = null;
		}
	}

	private bool EnsureDetector(Size inputSize)
	{
		if (!_modelInfo.IsReady)
		{
			_initializationStatus = _modelInfo.Status;
			return false;
		}
		if (_detector != null && _inputSize == inputSize)
		{
			return true;
		}
		try
		{
			_detector?.Dispose();
			_detector = FaceDetectorYN.Create(_modelInfo.ModelPath, "", inputSize, 0.7f);
			_inputSize = inputSize;
			_initializationStatus = "OpenCV YuNet face detector loaded";
			return true;
		}
		catch (Exception ex)
		{
			_detector?.Dispose();
			_detector = null;
			_inputSize = default(Size);
			_initializationStatus = "OpenCV YuNet face detector failed to load: " + ex.Message;
			return false;
		}
	}

	private static IReadOnlyList<YuNetFaceDetection> ParseFaces(Mat faces, int width, int height)
	{
		int rows = faces.Rows;
		int cols = faces.Cols;
		if (faces.Empty() || rows <= 0 || cols < 15)
		{
			return Array.Empty<YuNetFaceDetection>();
		}
		List<YuNetFaceDetection> list = new List<YuNetFaceDetection>(rows);
		for (int i = 0; i < rows; i++)
		{
			float num = faces.At<float>(i, 14);
			float num2 = faces.At<float>(i, 0);
			float num3 = faces.At<float>(i, 1);
			float num4 = faces.At<float>(i, 2);
			Rect faceBox = ClampRect(new Rect(Height: (int)Math.Round(faces.At<float>(i, 3)), X: (int)Math.Round(num2), Y: (int)Math.Round(num3), Width: (int)Math.Round(num4)), width, height);
			if (faceBox.Width > 0 && faceBox.Height > 0)
			{
				list.Add(new YuNetFaceDetection(faceBox, new Point2f(faces.At<float>(i, 4), faces.At<float>(i, 5)), new Point2f(faces.At<float>(i, 6), faces.At<float>(i, 7)), new Point2f(faces.At<float>(i, 8), faces.At<float>(i, 9)), new Point2f(faces.At<float>(i, 10), faces.At<float>(i, 11)), new Point2f(faces.At<float>(i, 12), faces.At<float>(i, 13)), Math.Clamp(num, 0.0, 1.0)));
			}
		}
		return list.OrderByDescending((YuNetFaceDetection detection) => detection.Score).ToList();
	}

	private static Rect ClampRect(Rect rect, int width, int height)
	{
		int num = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
		int num2 = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
		int num3 = Math.Clamp(rect.Right, num + 1, width);
		int num4 = Math.Clamp(rect.Bottom, num2 + 1, height);
		return new Rect(num, num2, num3 - num, num4 - num2);
	}
}
