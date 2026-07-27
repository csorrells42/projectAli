using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

/// <summary>
/// Reconstructs MediaPipe's canonical-face transformation matrix from the
/// normalized landmarks returned by the official MediaPipe Tasks model. The canonical
/// mesh and its 33 weighted rigid anchors come from the same face_landmarker.task
/// bundle used by the CPU MediaPipe Tasks graph.
/// </summary>
internal sealed class MediaPipeFaceGeometryEstimator
{
	private const int CanonicalLandmarkCount = 468;

	private const int VertexStride = 5;

	private const double NearPlane = 1.0;

	private const double VerticalFieldOfViewDegrees = 63.0;

	private readonly GeometryPoint[] _canonicalLandmarks;

	private readonly WeightedLandmark[] _weightedLandmarks;

	private readonly double _totalWeight;

	private readonly record struct GeometryPoint(double X, double Y, double Z);

	private readonly record struct WeightedLandmark(int Index, double Weight);

	private readonly record struct SimilarityTransform(
		double M00,
		double M01,
		double M02,
		double M10,
		double M11,
		double M12,
		double M20,
		double M21,
		double M22,
		double Tx,
		double Ty,
		double Tz,
		double Scale)
	{
		public double[] ToRowMajorMatrix()
		{
			return
			[
				M00, M01, M02, Tx,
				M10, M11, M12, Ty,
				M20, M21, M22, Tz,
				0.0, 0.0, 0.0, 1.0
			];
		}
	}

	private MediaPipeFaceGeometryEstimator(
		GeometryPoint[] canonicalLandmarks,
		WeightedLandmark[] weightedLandmarks)
	{
		_canonicalLandmarks = canonicalLandmarks;
		_weightedLandmarks = weightedLandmarks;
		double totalWeight = 0.0;
		foreach (WeightedLandmark landmark in weightedLandmarks)
		{
			totalWeight += landmark.Weight;
		}
		_totalWeight = totalWeight;
	}

	public static MediaPipeFaceGeometryEstimator Load(string taskModelPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(taskModelPath);
		using ZipArchive archive = ZipFile.OpenRead(taskModelPath);
		ZipArchiveEntry entry = archive.GetEntry(
			"geometry_pipeline_metadata_landmarks.binarypb")
			?? throw new InvalidDataException(
				"face_landmarker.task does not contain its canonical face geometry.");
		using Stream stream = entry.Open();
		using MemoryStream buffer = new((int)entry.Length);
		stream.CopyTo(buffer);
		return ParseMetadata(buffer.ToArray());
	}

	public bool TryEstimate(
		IReadOnlyList<MediaPipeSidecarLandmark> landmarks,
		int frameWidth,
		int frameHeight,
		out double[] transformationMatrix)
	{
		transformationMatrix = Array.Empty<double>();
		if (landmarks.Count < CanonicalLandmarkCount
			|| frameWidth <= 0
			|| frameHeight <= 0)
		{
			return false;
		}

		Span<GeometryPoint> projected =
			stackalloc GeometryPoint[CanonicalLandmarkCount];
		ProjectToNearPlane(
			landmarks,
			frameWidth,
			frameHeight,
			projected,
			out double depthOffset);

		for (int index = 0; index < projected.Length; index++)
		{
			GeometryPoint point = projected[index];
			projected[index] = new GeometryPoint(point.X, point.Y, -point.Z);
		}
		if (!TrySolveWeightedSimilarity(projected, out SimilarityTransform first)
			|| first.Scale <= 1e-9)
		{
			return false;
		}

		ProjectToNearPlane(
			landmarks,
			frameWidth,
			frameHeight,
			projected,
			out _);
		MoveRescaleUnprojectAndChangeHandedness(
			projected,
			depthOffset,
			first.Scale);
		if (!TrySolveWeightedSimilarity(projected, out SimilarityTransform second)
			|| second.Scale <= 1e-9)
		{
			return false;
		}

		double totalScale = first.Scale * second.Scale;
		if (!double.IsFinite(totalScale) || totalScale <= 1e-9)
		{
			return false;
		}
		ProjectToNearPlane(
			landmarks,
			frameWidth,
			frameHeight,
			projected,
			out _);
		MoveRescaleUnprojectAndChangeHandedness(
			projected,
			depthOffset,
			totalScale);
		if (!TrySolveWeightedSimilarity(projected, out SimilarityTransform final))
		{
			return false;
		}

		transformationMatrix = final.ToRowMajorMatrix();
		for (int index = 0; index < transformationMatrix.Length; index++)
		{
			if (!double.IsFinite(transformationMatrix[index]))
			{
				transformationMatrix = Array.Empty<double>();
				return false;
			}
		}
		return true;
	}

	internal bool RunDeterministicSelfTest(out double maximumError)
	{
		const int frameWidth = 1920;
		const int frameHeight = 1080;
		const double pitchDegrees = -12.0;
		const double yawDegrees = 27.0;
		const double rollDegrees = 8.0;
		double pitch = pitchDegrees * Math.PI / 180.0;
		double yaw = yawDegrees * Math.PI / 180.0;
		double roll = rollDegrees * Math.PI / 180.0;
		double cp = Math.Cos(pitch);
		double sp = Math.Sin(pitch);
		double cy = Math.Cos(yaw);
		double sy = Math.Sin(yaw);
		double cr = Math.Cos(roll);
		double sr = Math.Sin(roll);
		double r00 = cr * cy;
		double r01 = cr * sy * sp - sr * cp;
		double r02 = cr * sy * cp + sr * sp;
		double r10 = sr * cy;
		double r11 = sr * sy * sp + cr * cp;
		double r12 = sr * sy * cp - cr * sp;
		double r20 = -sy;
		double r21 = cy * sp;
		double r22 = cy * cp;
		const double tx = 2.0;
		const double ty = -1.0;
		const double tz = -55.0;

		double heightAtNear = 2.0 * NearPlane
			* Math.Tan(VerticalFieldOfViewDegrees * Math.PI / 360.0);
		double widthAtNear =
			frameWidth * heightAtNear / frameHeight;
		GeometryPoint[] metric = new GeometryPoint[CanonicalLandmarkCount];
		double meanDepth = 0.0;
		for (int index = 0; index < metric.Length; index++)
		{
			GeometryPoint source = _canonicalLandmarks[index];
			GeometryPoint target = new(
				r00 * source.X + r01 * source.Y + r02 * source.Z + tx,
				r10 * source.X + r11 * source.Y + r12 * source.Z + ty,
				r20 * source.X + r21 * source.Y + r22 * source.Z + tz);
			metric[index] = target;
			meanDepth += -target.Z;
		}
		meanDepth /= metric.Length;
		double screenDepthScale = NearPlane / meanDepth;
		MediaPipeSidecarLandmark[] screen =
			new MediaPipeSidecarLandmark[CanonicalLandmarkCount];
		for (int index = 0; index < screen.Length; index++)
		{
			GeometryPoint target = metric[index];
			double positiveDepth = -target.Z;
			double nearX = target.X * NearPlane / positiveDepth;
			double nearY = target.Y * NearPlane / positiveDepth;
			double screenZ =
				(-NearPlane + screenDepthScale * positiveDepth)
				/ widthAtNear;
			screen[index] = new MediaPipeSidecarLandmark(
				(nearX + widthAtNear * 0.5) / widthAtNear,
				1.0 - (nearY + heightAtNear * 0.5) / heightAtNear,
				screenZ);
		}

		if (!TryEstimate(
			screen,
			frameWidth,
			frameHeight,
			out double[] matrix))
		{
			maximumError = double.PositiveInfinity;
			return false;
		}
		double[] expected =
		[
			r00, r01, r02, tx,
			r10, r11, r12, ty,
			r20, r21, r22, tz,
			0.0, 0.0, 0.0, 1.0
		];
		maximumError = 0.0;
		for (int index = 0; index < expected.Length; index++)
		{
			maximumError = Math.Max(
				maximumError,
				Math.Abs(matrix[index] - expected[index]));
		}
		return maximumError < 1e-7;
	}

	internal int WeightedLandmarkCount => _weightedLandmarks.Length;

	private void ProjectToNearPlane(
		IReadOnlyList<MediaPipeSidecarLandmark> landmarks,
		int frameWidth,
		int frameHeight,
		Span<GeometryPoint> projected,
		out double depthOffset)
	{
		double heightAtNear = 2.0 * NearPlane
			* Math.Tan(VerticalFieldOfViewDegrees * Math.PI / 360.0);
		double widthAtNear =
			frameWidth * heightAtNear / frameHeight;
		double left = -widthAtNear * 0.5;
		double bottom = -heightAtNear * 0.5;
		depthOffset = 0.0;
		for (int index = 0; index < projected.Length; index++)
		{
			MediaPipeSidecarLandmark landmark = landmarks[index];
			double z = landmark.Z * widthAtNear;
			projected[index] = new GeometryPoint(
				landmark.X * widthAtNear + left,
				(1.0 - landmark.Y) * heightAtNear + bottom,
				z);
			depthOffset += z;
		}
		depthOffset /= projected.Length;
	}

	private static void MoveRescaleUnprojectAndChangeHandedness(
		Span<GeometryPoint> landmarks,
		double depthOffset,
		double scale)
	{
		for (int index = 0; index < landmarks.Length; index++)
		{
			GeometryPoint point = landmarks[index];
			double z =
				(point.Z - depthOffset + NearPlane) / scale;
			landmarks[index] = new GeometryPoint(
				point.X * z / NearPlane,
				point.Y * z / NearPlane,
				-z);
		}
	}

	private bool TrySolveWeightedSimilarity(
		ReadOnlySpan<GeometryPoint> targets,
		out SimilarityTransform transform)
	{
		transform = default;
		if (targets.Length < CanonicalLandmarkCount
			|| _weightedLandmarks.Length == 0
			|| _totalWeight <= 1e-12)
		{
			return false;
		}

		double sourceCenterX = 0.0;
		double sourceCenterY = 0.0;
		double sourceCenterZ = 0.0;
		double targetCenterX = 0.0;
		double targetCenterY = 0.0;
		double targetCenterZ = 0.0;
		foreach (WeightedLandmark basis in _weightedLandmarks)
		{
			GeometryPoint source = _canonicalLandmarks[basis.Index];
			GeometryPoint target = targets[basis.Index];
			sourceCenterX += basis.Weight * source.X;
			sourceCenterY += basis.Weight * source.Y;
			sourceCenterZ += basis.Weight * source.Z;
			targetCenterX += basis.Weight * target.X;
			targetCenterY += basis.Weight * target.Y;
			targetCenterZ += basis.Weight * target.Z;
		}
		sourceCenterX /= _totalWeight;
		sourceCenterY /= _totalWeight;
		sourceCenterZ /= _totalWeight;
		targetCenterX /= _totalWeight;
		targetCenterY /= _totalWeight;
		targetCenterZ /= _totalWeight;

		double sxx = 0.0;
		double sxy = 0.0;
		double sxz = 0.0;
		double syx = 0.0;
		double syy = 0.0;
		double syz = 0.0;
		double szx = 0.0;
		double szy = 0.0;
		double szz = 0.0;
		double denominator = 0.0;
		foreach (WeightedLandmark basis in _weightedLandmarks)
		{
			GeometryPoint source = _canonicalLandmarks[basis.Index];
			GeometryPoint target = targets[basis.Index];
			double ax = source.X - sourceCenterX;
			double ay = source.Y - sourceCenterY;
			double az = source.Z - sourceCenterZ;
			double bx = target.X - targetCenterX;
			double by = target.Y - targetCenterY;
			double bz = target.Z - targetCenterZ;
			double weight = basis.Weight;
			sxx += weight * ax * bx;
			sxy += weight * ax * by;
			sxz += weight * ax * bz;
			syx += weight * ay * bx;
			syy += weight * ay * by;
			syz += weight * ay * bz;
			szx += weight * az * bx;
			szy += weight * az * by;
			szz += weight * az * bz;
			denominator += weight * (ax * ax + ay * ay + az * az);
		}
		if (denominator <= 1e-12
			|| !TryFindDominantQuaternion(
				sxx,
				sxy,
				sxz,
				syx,
				syy,
				syz,
				szx,
				szy,
				szz,
				out double qw,
				out double qx,
				out double qy,
				out double qz))
		{
			return false;
		}

		double m00 = 1.0 - 2.0 * (qy * qy + qz * qz);
		double m01 = 2.0 * (qx * qy - qz * qw);
		double m02 = 2.0 * (qx * qz + qy * qw);
		double m10 = 2.0 * (qx * qy + qz * qw);
		double m11 = 1.0 - 2.0 * (qx * qx + qz * qz);
		double m12 = 2.0 * (qy * qz - qx * qw);
		double m20 = 2.0 * (qx * qz - qy * qw);
		double m21 = 2.0 * (qy * qz + qx * qw);
		double m22 = 1.0 - 2.0 * (qx * qx + qy * qy);

		double numerator = 0.0;
		foreach (WeightedLandmark basis in _weightedLandmarks)
		{
			GeometryPoint source = _canonicalLandmarks[basis.Index];
			GeometryPoint target = targets[basis.Index];
			double ax = source.X - sourceCenterX;
			double ay = source.Y - sourceCenterY;
			double az = source.Z - sourceCenterZ;
			double rotatedX = m00 * ax + m01 * ay + m02 * az;
			double rotatedY = m10 * ax + m11 * ay + m12 * az;
			double rotatedZ = m20 * ax + m21 * ay + m22 * az;
			numerator += basis.Weight * (
				rotatedX * (target.X - targetCenterX)
				+ rotatedY * (target.Y - targetCenterY)
				+ rotatedZ * (target.Z - targetCenterZ));
		}
		double scale = numerator / denominator;
		if (!double.IsFinite(scale) || scale <= 1e-9)
		{
			return false;
		}

		double tx = targetCenterX - scale * (
			m00 * sourceCenterX
			+ m01 * sourceCenterY
			+ m02 * sourceCenterZ);
		double ty = targetCenterY - scale * (
			m10 * sourceCenterX
			+ m11 * sourceCenterY
			+ m12 * sourceCenterZ);
		double tz = targetCenterZ - scale * (
			m20 * sourceCenterX
			+ m21 * sourceCenterY
			+ m22 * sourceCenterZ);
		transform = new SimilarityTransform(
			scale * m00,
			scale * m01,
			scale * m02,
			scale * m10,
			scale * m11,
			scale * m12,
			scale * m20,
			scale * m21,
			scale * m22,
			tx,
			ty,
			tz,
			scale);
		return true;
	}

	private static bool TryFindDominantQuaternion(
		double sxx,
		double sxy,
		double sxz,
		double syx,
		double syy,
		double syz,
		double szx,
		double szy,
		double szz,
		out double qw,
		out double qx,
		out double qy,
		out double qz)
	{
		Span<double> matrix = stackalloc double[16];
		matrix[0] = sxx + syy + szz;
		matrix[1] = syz - szy;
		matrix[2] = szx - sxz;
		matrix[3] = sxy - syx;
		matrix[4] = matrix[1];
		matrix[5] = sxx - syy - szz;
		matrix[6] = sxy + syx;
		matrix[7] = szx + sxz;
		matrix[8] = matrix[2];
		matrix[9] = matrix[6];
		matrix[10] = -sxx + syy - szz;
		matrix[11] = syz + szy;
		matrix[12] = matrix[3];
		matrix[13] = matrix[7];
		matrix[14] = matrix[11];
		matrix[15] = -sxx - syy + szz;

		double shift = 1e-12;
		for (int row = 0; row < 4; row++)
		{
			double rowMagnitude = 0.0;
			for (int column = 0; column < 4; column++)
			{
				rowMagnitude += Math.Abs(matrix[row * 4 + column]);
			}
			shift = Math.Max(shift, rowMagnitude);
		}
		for (int row = 0; row < 4; row++)
		{
			matrix[row * 4 + row] += shift;
		}

		Span<double> value = stackalloc double[4]
		{
			1.0,
			0.5,
			0.25,
			0.125
		};
		NormalizeQuaternion(value);
		Span<double> next = stackalloc double[4];
		for (int iteration = 0; iteration < 64; iteration++)
		{
			for (int row = 0; row < 4; row++)
			{
				next[row] = 0.0;
				for (int column = 0; column < 4; column++)
				{
					next[row] += matrix[row * 4 + column] * value[column];
				}
			}
			if (!NormalizeQuaternion(next))
			{
				qw = qx = qy = qz = 0.0;
				return false;
			}
			next.CopyTo(value);
		}
		qw = value[0];
		qx = value[1];
		qy = value[2];
		qz = value[3];
		return true;
	}

	private static bool NormalizeQuaternion(Span<double> value)
	{
		double length = Math.Sqrt(
			value[0] * value[0]
			+ value[1] * value[1]
			+ value[2] * value[2]
			+ value[3] * value[3]);
		if (!double.IsFinite(length) || length <= 1e-15)
		{
			return false;
		}
		for (int index = 0; index < value.Length; index++)
		{
			value[index] /= length;
		}
		return true;
	}

	private static MediaPipeFaceGeometryEstimator ParseMetadata(byte[] data)
	{
		ReadOnlySpan<byte> metadata = data;
		GeometryPoint[]? canonicalLandmarks = null;
		List<WeightedLandmark> weightedLandmarks = new(33);
		int offset = 0;
		while (offset < metadata.Length)
		{
			ulong tag = ReadVarint(metadata, ref offset);
			int fieldNumber = (int)(tag >> 3);
			int wireType = (int)(tag & 7);
			if (wireType == 2)
			{
				ReadOnlySpan<byte> value = ReadLengthDelimited(
					metadata,
					ref offset);
				if (fieldNumber == 1)
				{
					canonicalLandmarks = ParseCanonicalMesh(value);
				}
				else if (fieldNumber == 2)
				{
					weightedLandmarks.Add(ParseWeightedLandmark(value));
				}
				continue;
			}
			SkipValue(metadata, ref offset, wireType);
		}
		if (canonicalLandmarks == null)
		{
			throw new InvalidDataException(
				"Canonical face mesh is missing from MediaPipe geometry metadata.");
		}
		if (canonicalLandmarks.Length != CanonicalLandmarkCount
			|| weightedLandmarks.Count != 33)
		{
			throw new InvalidDataException(
				$"MediaPipe canonical geometry contains " +
				$"{canonicalLandmarks.Length} vertices and " +
				$"{weightedLandmarks.Count} weighted anchors; " +
				$"468 vertices and 33 anchors are required.");
		}
		return new MediaPipeFaceGeometryEstimator(
			canonicalLandmarks,
			weightedLandmarks.ToArray());
	}

	private static GeometryPoint[] ParseCanonicalMesh(
		ReadOnlySpan<byte> mesh)
	{
		List<float> vertexBuffer =
			new(CanonicalLandmarkCount * VertexStride);
		int offset = 0;
		while (offset < mesh.Length)
		{
			ulong tag = ReadVarint(mesh, ref offset);
			int fieldNumber = (int)(tag >> 3);
			int wireType = (int)(tag & 7);
			if (fieldNumber == 3 && wireType == 5)
			{
				EnsureAvailable(mesh, offset, sizeof(uint));
				uint bits = BinaryPrimitives.ReadUInt32LittleEndian(
					mesh.Slice(offset, sizeof(uint)));
				offset += sizeof(uint);
				vertexBuffer.Add(BitConverter.Int32BitsToSingle((int)bits));
				continue;
			}
			SkipValue(mesh, ref offset, wireType);
		}
		if (vertexBuffer.Count % VertexStride != 0)
		{
			throw new InvalidDataException(
				"MediaPipe canonical vertex buffer is incomplete.");
		}
		GeometryPoint[] points =
			new GeometryPoint[vertexBuffer.Count / VertexStride];
		for (int index = 0; index < points.Length; index++)
		{
			int baseIndex = index * VertexStride;
			points[index] = new GeometryPoint(
				vertexBuffer[baseIndex],
				vertexBuffer[baseIndex + 1],
				vertexBuffer[baseIndex + 2]);
		}
		return points;
	}

	private static WeightedLandmark ParseWeightedLandmark(
		ReadOnlySpan<byte> message)
	{
		int index = -1;
		double weight = 0.0;
		int offset = 0;
		while (offset < message.Length)
		{
			ulong tag = ReadVarint(message, ref offset);
			int fieldNumber = (int)(tag >> 3);
			int wireType = (int)(tag & 7);
			if (fieldNumber == 1 && wireType == 0)
			{
				index = checked((int)ReadVarint(message, ref offset));
				continue;
			}
			if (fieldNumber == 2 && wireType == 5)
			{
				EnsureAvailable(message, offset, sizeof(uint));
				uint bits = BinaryPrimitives.ReadUInt32LittleEndian(
					message.Slice(offset, sizeof(uint)));
				offset += sizeof(uint);
				weight = BitConverter.Int32BitsToSingle((int)bits);
				continue;
			}
			SkipValue(message, ref offset, wireType);
		}
		if ((uint)index >= CanonicalLandmarkCount
			|| !double.IsFinite(weight)
			|| weight <= 0.0)
		{
			throw new InvalidDataException(
				"MediaPipe canonical geometry contains an invalid weighted anchor.");
		}
		return new WeightedLandmark(index, weight);
	}

	private static ReadOnlySpan<byte> ReadLengthDelimited(
		ReadOnlySpan<byte> data,
		ref int offset)
	{
		int length = checked((int)ReadVarint(data, ref offset));
		EnsureAvailable(data, offset, length);
		ReadOnlySpan<byte> value = data.Slice(offset, length);
		offset += length;
		return value;
	}

	private static ulong ReadVarint(
		ReadOnlySpan<byte> data,
		ref int offset)
	{
		ulong value = 0;
		for (int shift = 0; shift < 64; shift += 7)
		{
			EnsureAvailable(data, offset, 1);
			byte current = data[offset++];
			value |= (ulong)(current & 0x7f) << shift;
			if ((current & 0x80) == 0)
			{
				return value;
			}
		}
		throw new InvalidDataException(
			"MediaPipe geometry metadata contains an invalid varint.");
	}

	private static void SkipValue(
		ReadOnlySpan<byte> data,
		ref int offset,
		int wireType)
	{
		switch (wireType)
		{
			case 0:
				ReadVarint(data, ref offset);
				break;
			case 1:
				EnsureAvailable(data, offset, sizeof(ulong));
				offset += sizeof(ulong);
				break;
			case 2:
				int length = checked((int)ReadVarint(data, ref offset));
				EnsureAvailable(data, offset, length);
				offset += length;
				break;
			case 5:
				EnsureAvailable(data, offset, sizeof(uint));
				offset += sizeof(uint);
				break;
			default:
				throw new InvalidDataException(
					$"MediaPipe geometry metadata uses unsupported wire type {wireType}.");
		}
	}

	private static void EnsureAvailable(
		ReadOnlySpan<byte> data,
		int offset,
		int length)
	{
		if (offset < 0
			|| length < 0
			|| offset > data.Length - length)
		{
			throw new EndOfStreamException(
				"MediaPipe geometry metadata ended unexpectedly.");
		}
	}
}
