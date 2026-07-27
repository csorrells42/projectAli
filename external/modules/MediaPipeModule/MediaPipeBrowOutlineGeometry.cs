using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

internal static class MediaPipeBrowOutlineGeometry
{
	private readonly record struct IndexedPoint(int Index, double X, double Y);

	private const double GeometryEpsilon = 1E-10;

	internal static readonly int[] BrowAIndices = new int[10] { 70, 63, 105, 66, 107, 55, 65, 52, 53, 46 };

	internal static readonly int[] BrowBIndices = new int[10] { 336, 296, 334, 293, 300, 285, 295, 282, 283, 276 };

	public static IReadOnlyList<int> BuildClosedOutlineIndices(IReadOnlyList<FaceMeshLandmarkPoint> meshPoints, IReadOnlyList<int> candidateIndices)
	{
		if (meshPoints.Count < 3 || candidateIndices.Count < 3)
		{
			return Array.Empty<int>();
		}
		Span<IndexedPoint> points = candidateIndices.Count <= 64
			? stackalloc IndexedPoint[candidateIndices.Count]
			: new IndexedPoint[candidateIndices.Count];
		int pointCount = 0;
		for (int i = 0; i < candidateIndices.Count; i++)
		{
			int candidateIndex = candidateIndices[i];
			if (TryGetMeshPoint(meshPoints, candidateIndex, out FaceMeshLandmarkPoint value) &&
				double.IsFinite(value.X) &&
				double.IsFinite(value.Y))
			{
				points[pointCount++] = new IndexedPoint(candidateIndex, value.X, value.Y);
			}
		}
		Span<IndexedPoint> validPoints = points[..pointCount];
		SortPoints(validPoints);
		pointCount = RemoveDuplicateCoordinates(validPoints);
		if (pointCount < 3)
		{
			return Array.Empty<int>();
		}
		Span<IndexedPoint> uniquePoints = validPoints[..pointCount];
		Span<IndexedPoint> array = pointCount <= 64
			? stackalloc IndexedPoint[pointCount * 2]
			: new IndexedPoint[pointCount * 2];
		int count = 0;
		for (int i = 0; i < uniquePoints.Length; i++)
		{
			AppendHullPoint(array, ref count, uniquePoints[i]);
		}
		int num = count;
		for (int num2 = uniquePoints.Length - 2; num2 >= 0; num2--)
		{
			while (count > num && count >= 2 && Cross(array[count - 2], array[count - 1], uniquePoints[num2]) <= 1E-10)
			{
				count--;
			}
			array[count++] = uniquePoints[num2];
		}
		count--;
		if (count < 3)
		{
			return Array.Empty<int>();
		}
		int[] array2 = new int[count];
		for (int num3 = 0; num3 < count; num3++)
		{
			array2[num3] = array[num3].Index;
		}
		if (!TryValidateClosedOutline(meshPoints, array2, closed: true, out string _))
		{
			return Array.Empty<int>();
		}
		return array2;
	}

	public static bool TryValidateClosedOutline(IReadOnlyList<FaceMeshLandmarkPoint> meshPoints, IReadOnlyList<int> outlineIndices, bool closed, out string failureReason)
	{
		if (!closed)
		{
			failureReason = "The eyebrow outline is open.";
			return false;
		}
		if (outlineIndices.Count < 3 || ContainsDuplicateIndices(outlineIndices))
		{
			failureReason = "The eyebrow outline needs at least three unique vertices.";
			return false;
		}
		Span<IndexedPoint> array = outlineIndices.Count <= 64
			? stackalloc IndexedPoint[outlineIndices.Count]
			: new IndexedPoint[outlineIndices.Count];
		for (int i = 0; i < outlineIndices.Count; i++)
		{
			if (!TryGetMeshPoint(meshPoints, outlineIndices[i], out FaceMeshLandmarkPoint value) ||
				!double.IsFinite(value.X) ||
				!double.IsFinite(value.Y))
			{
				failureReason = $"Eyebrow vertex {outlineIndices[i]} is missing or invalid.";
				return false;
			}
			array[i] = new IndexedPoint(value.Index, value.X, value.Y);
			IndexedPoint left = array[(i + array.Length - 1) % array.Length];
			if (i > 0 && SamePoint(left, array[i]))
			{
				failureReason = "The eyebrow outline contains a zero-length edge.";
				return false;
			}
		}
		if (SamePoint(array[^1], array[0]))
		{
			failureReason = "The eyebrow outline contains a zero-length closing edge.";
			return false;
		}
		for (int j = 0; j < array.Length; j++)
		{
			int num = (j + 1) % array.Length;
			for (int k = j + 1; k < array.Length; k++)
			{
				int num2 = (k + 1) % array.Length;
				if (j != k && num != k && num2 != j && SegmentsIntersect(array[j], array[num], array[k], array[num2]))
				{
					failureReason = $"Eyebrow edges {j} and {k} cross.";
					return false;
				}
			}
		}
		double num3 = 0.0;
		for (int l = 0; l < array.Length; l++)
		{
			IndexedPoint indexedPoint = array[(l + 1) % array.Length];
			num3 += array[l].X * indexedPoint.Y - indexedPoint.X * array[l].Y;
		}
		if (Math.Abs(num3) <= 1E-10)
		{
			failureReason = "The eyebrow outline has no enclosed area.";
			return false;
		}
		failureReason = string.Empty;
		return true;
	}

	private static void SortPoints(Span<IndexedPoint> points)
	{
		for (int i = 1; i < points.Length; i++)
		{
			IndexedPoint point = points[i];
			int insertAt = i - 1;
			while (insertAt >= 0 && Compare(points[insertAt], point) > 0)
			{
				points[insertAt + 1] = points[insertAt];
				insertAt--;
			}
			points[insertAt + 1] = point;
		}
	}

	private static int Compare(IndexedPoint left, IndexedPoint right)
	{
		int result = left.X.CompareTo(right.X);
		if (result != 0)
		{
			return result;
		}
		result = left.Y.CompareTo(right.Y);
		return result != 0 ? result : left.Index.CompareTo(right.Index);
	}

	private static int RemoveDuplicateCoordinates(Span<IndexedPoint> points)
	{
		int num = 1;
		for (int i = 1; i < points.Length; i++)
		{
			if (!SamePoint(points[i], points[num - 1]))
			{
				points[num++] = points[i];
			}
		}
		return num;
	}

	private static bool ContainsDuplicateIndices(IReadOnlyList<int> indices)
	{
		for (int i = 0; i < indices.Count - 1; i++)
		{
			for (int j = i + 1; j < indices.Count; j++)
			{
				if (indices[i] == indices[j])
				{
					return true;
				}
			}
		}
		return false;
	}

	private static bool TryGetMeshPoint(
		IReadOnlyList<FaceMeshLandmarkPoint> meshPoints,
		int index,
		out FaceMeshLandmarkPoint point)
	{
		if ((uint)index < (uint)meshPoints.Count)
		{
			point = meshPoints[index];
			if (point.Index == index)
			{
				return true;
			}
		}
		for (int i = 0; i < meshPoints.Count; i++)
		{
			point = meshPoints[i];
			if (point.Index == index)
			{
				return true;
			}
		}
		point = default;
		return false;
	}

	private static void AppendHullPoint(Span<IndexedPoint> hull, ref int count, IndexedPoint point)
	{
		while (count >= 2 && Cross(hull[count - 2], hull[count - 1], point) <= 1E-10)
		{
			count--;
		}
		hull[count++] = point;
	}

	private static double Cross(IndexedPoint origin, IndexedPoint a, IndexedPoint b)
	{
		return (a.X - origin.X) * (b.Y - origin.Y) - (a.Y - origin.Y) * (b.X - origin.X);
	}

	private static bool SegmentsIntersect(IndexedPoint a, IndexedPoint b, IndexedPoint c, IndexedPoint d)
	{
		double num = Cross(a, b, c);
		double num2 = Cross(a, b, d);
		double num3 = Cross(c, d, a);
		double num4 = Cross(c, d, b);
		if (((num > 1E-10 && num2 < -1E-10) || (num < -1E-10 && num2 > 1E-10)) && ((num3 > 1E-10 && num4 < -1E-10) || (num3 < -1E-10 && num4 > 1E-10)))
		{
			return true;
		}
		if ((!(Math.Abs(num) <= 1E-10) || !IsOnSegment(a, b, c)) && (!(Math.Abs(num2) <= 1E-10) || !IsOnSegment(a, b, d)) && (!(Math.Abs(num3) <= 1E-10) || !IsOnSegment(c, d, a)))
		{
			if (Math.Abs(num4) <= 1E-10)
			{
				return IsOnSegment(c, d, b);
			}
			return false;
		}
		return true;
	}

	private static bool IsOnSegment(IndexedPoint start, IndexedPoint end, IndexedPoint point)
	{
		if (point.X >= Math.Min(start.X, end.X) - 1E-10 && point.X <= Math.Max(start.X, end.X) + 1E-10 && point.Y >= Math.Min(start.Y, end.Y) - 1E-10)
		{
			return point.Y <= Math.Max(start.Y, end.Y) + 1E-10;
		}
		return false;
	}

	private static bool SamePoint(IndexedPoint left, IndexedPoint right)
	{
		if (Math.Abs(left.X - right.X) <= 1E-10)
		{
			return Math.Abs(left.Y - right.Y) <= 1E-10;
		}
		return false;
	}
}
