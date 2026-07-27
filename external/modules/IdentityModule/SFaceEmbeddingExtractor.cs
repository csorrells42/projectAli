using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.OpenCv;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace AvatarBuilder.Modules.Vision.Identity;

/// <summary>
/// Produces normalized SFace embeddings from YuNet's five measured face points.
/// It stores no image and owns no identity policy.
/// </summary>
internal sealed class SFaceEmbeddingExtractor : IDisposable
{
	public const int ExpectedEmbeddingLength = 128;

	private const int AlignedFaceSize = 112;

	private static readonly Point2f[] AlignmentTarget =
	[
		new(38.2946f, 51.6963f),
		new(73.5318f, 51.5014f),
		new(56.0252f, 71.7366f),
		new(41.5493f, 92.3655f),
		new(70.7299f, 92.2041f)
	];

	private readonly Net _network;

	private bool _usingOpenCl;

	private bool _disposed;

	public string BackendName => _usingOpenCl
		? "OpenCV SFace OpenCL"
		: "OpenCV SFace CPU";

	public SFaceEmbeddingExtractor(string modelPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
		Net? network = CvDnn.ReadNetFromOnnx(modelPath);
		if (network is null || network.Empty())
		{
			network?.Dispose();
			throw new InvalidOperationException(
				"OpenCV loaded an empty SFace network.");
		}
		_network = network;
		TryEnableOpenCl();
		WarmUp();
	}

	public bool TryExtract(
		Mat bgrFrame,
		YuNetFaceDetection face,
		out float[] embedding)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		embedding = [];
		if (bgrFrame.Empty()
			|| face.FaceBox.Width < 48
			|| face.FaceBox.Height < 48)
		{
			return false;
		}

		using Mat aligned = AlignFace(bgrFrame, face);
		if (aligned.Empty())
		{
			return false;
		}
		using Mat blob = CvDnn.BlobFromImage(
			aligned,
			1d,
			new Size(AlignedFaceSize, AlignedFaceSize),
			Scalar.All(0d),
			swapRB: true,
			crop: false);
		using Mat features = Forward(blob);
		int valueCount = checked((int)features.Total());
		if (valueCount != ExpectedEmbeddingLength)
		{
			return false;
		}
		float[] values = new float[valueCount];
		features.GetArray(out values);
		double squaredNorm = 0d;
		for (int index = 0; index < values.Length; index++)
		{
			squaredNorm += values[index] * values[index];
		}
		double norm = Math.Sqrt(squaredNorm);
		if (!double.IsFinite(norm) || norm < 1e-8d)
		{
			return false;
		}
		float inverseNorm = (float)(1d / norm);
		for (int index = 0; index < values.Length; index++)
		{
			values[index] *= inverseNorm;
		}
		embedding = values;
		return true;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_network.Dispose();
	}

	private Mat Forward(Mat blob)
	{
		try
		{
			_network.SetInput(blob);
			return _network.Forward("");
		}
		catch when (_usingOpenCl)
		{
			_network.SetPreferableBackend(Backend.OPENCV);
			_network.SetPreferableTarget(Target.CPU);
			_usingOpenCl = false;
			_network.SetInput(blob);
			return _network.Forward("");
		}
	}

	private void TryEnableOpenCl()
	{
		try
		{
			_network.SetPreferableBackend(Backend.OPENCV);
			_network.SetPreferableTarget(Target.OPENCL);
			_usingOpenCl = true;
		}
		catch
		{
			_network.SetPreferableBackend(Backend.OPENCV);
			_network.SetPreferableTarget(Target.CPU);
			_usingOpenCl = false;
		}
	}

	private void WarmUp()
	{
		using Mat image = new(
			AlignedFaceSize,
			AlignedFaceSize,
			MatType.CV_8UC3,
			Scalar.All(127d));
		using Mat blob = CvDnn.BlobFromImage(
			image,
			1d,
			new Size(AlignedFaceSize, AlignedFaceSize),
			Scalar.All(0d),
			swapRB: true,
			crop: false);
		using Mat output = Forward(blob);
		if (output.Total() != ExpectedEmbeddingLength)
		{
			throw new InvalidOperationException(
				$"SFace returned {output.Total()} values; " +
				$"{ExpectedEmbeddingLength} are required.");
		}
	}

	private static Mat AlignFace(
		Mat frame,
		YuNetFaceDetection face)
	{
		Point2f[] source =
		[
			face.RightEye,
			face.LeftEye,
			face.NoseTip,
			face.RightMouthCorner,
			face.LeftMouthCorner
		];
		using Mat transform = CreateSimilarityTransform(
			source,
			AlignmentTarget);
		Mat aligned = new();
		Cv2.WarpAffine(
			frame,
			aligned,
			transform,
			new Size(AlignedFaceSize, AlignedFaceSize),
			InterpolationFlags.Linear,
			BorderTypes.Constant,
			Scalar.All(0d));
		return aligned;
	}

	private static Mat CreateSimilarityTransform(
		IReadOnlyList<Point2f> source,
		IReadOnlyList<Point2f> destination)
	{
		if (source.Count != destination.Count || source.Count == 0)
		{
			throw new ArgumentException(
				"Similarity alignment requires matching point sets.");
		}
		double sourceMeanX = 0d;
		double sourceMeanY = 0d;
		double destinationMeanX = 0d;
		double destinationMeanY = 0d;
		for (int index = 0; index < source.Count; index++)
		{
			sourceMeanX += source[index].X;
			sourceMeanY += source[index].Y;
			destinationMeanX += destination[index].X;
			destinationMeanY += destination[index].Y;
		}
		sourceMeanX /= source.Count;
		sourceMeanY /= source.Count;
		destinationMeanX /= destination.Count;
		destinationMeanY /= destination.Count;

		double denominator = 0d;
		double aNumerator = 0d;
		double bNumerator = 0d;
		for (int index = 0; index < source.Count; index++)
		{
			double x = source[index].X - sourceMeanX;
			double y = source[index].Y - sourceMeanY;
			double u = destination[index].X - destinationMeanX;
			double v = destination[index].Y - destinationMeanY;
			denominator += x * x + y * y;
			aNumerator += x * u + y * v;
			bNumerator += x * v - y * u;
		}
		if (denominator < 1e-8d)
		{
			throw new InvalidOperationException(
				"Face alignment points are degenerate.");
		}
		double a = aNumerator / denominator;
		double b = bNumerator / denominator;
		double translateX =
			destinationMeanX - a * sourceMeanX + b * sourceMeanY;
		double translateY =
			destinationMeanY - b * sourceMeanX - a * sourceMeanY;
		Mat transform = new(2, 3, MatType.CV_64FC1);
		transform.Set(0, 0, a);
		transform.Set(0, 1, -b);
		transform.Set(0, 2, translateX);
		transform.Set(1, 0, b);
		transform.Set(1, 1, a);
		transform.Set(1, 2, translateY);
		return transform;
	}
}
