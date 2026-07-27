using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.MediaPipe;
using AvatarBuilder.Modules.Webcam.DirectX12;

namespace AvatarBuilder.Modules.Vision.Overlays;

public static class MediaPipePreviewOverlayFactory
{
	private static readonly int[] EyeA = new int[16]
	{
		33, 246, 161, 160, 159, 158, 157, 173, 133, 155,
		154, 153, 145, 144, 163, 7
	};

	private static readonly int[] EyeB = new int[16]
	{
		362, 398, 384, 385, 386, 387, 388, 466, 263, 249,
		390, 373, 374, 380, 381, 382
	};

	private static readonly int[] OuterLip = new int[20]
	{
		61, 185, 40, 39, 37, 0, 267, 269, 270, 409,
		291, 375, 321, 405, 314, 17, 84, 181, 91, 146
	};

	private static readonly int[] InnerLip = new int[20]
	{
		78, 191, 80, 81, 82, 13, 312, 311, 310, 415,
		308, 324, 318, 402, 317, 14, 87, 178, 88, 95
	};

	private static readonly int[] Jaw = new int[21]
	{
		234, 93, 132, 58, 172, 136, 150, 149, 176, 148,
		152, 377, 400, 378, 379, 365, 397, 288, 361, 323,
		454
	};

	private static readonly int[] NoseBridge = new int[10] { 168, 6, 197, 195, 5, 4, 1, 19, 94, 2 };

	private static readonly int[] NoseBase = new int[5] { 98, 97, 2, 326, 327 };

	private static readonly PreviewOverlayEdge[] Edges = CreateEdges();

	private static readonly PreviewOverlayIndexedPath[] BaseFeaturePaths = CreateBaseFeaturePaths();

	private static readonly bool[] FeaturePointMask = CreateFeaturePointMask();

	public static IReadOnlyList<int> EyeAIndices => EyeA;

	public static IReadOnlyList<int> EyeBIndices => EyeB;

	public static IReadOnlyList<PreviewOverlayEdge> MeshEdges => Edges;

	public static IReadOnlyList<bool> MeshFeaturePointMask =>
		FeaturePointMask;

	public static PreviewOverlayMesh? CreateMesh(
		IReadOnlyList<FaceMeshLandmarkPoint> denseMeshPoints)
	{
		if (denseMeshPoints.Count < 468)
		{
			return null;
		}
		PreviewOverlayPoint[] array =
			new PreviewOverlayPoint[denseMeshPoints.Count];
		Array.Fill(array, new PreviewOverlayPoint(double.NaN, double.NaN));
		foreach (FaceMeshLandmarkPoint denseMeshPoint in denseMeshPoints)
		{
			if ((uint)denseMeshPoint.Index < (uint)array.Length && double.IsFinite(denseMeshPoint.X) && double.IsFinite(denseMeshPoint.Y))
			{
				array[denseMeshPoint.Index] = new PreviewOverlayPoint(denseMeshPoint.X, denseMeshPoint.Y).Clamp();
			}
		}
		return new PreviewOverlayMesh(
			array,
			Edges,
			CreateFeaturePaths(denseMeshPoints),
			FeaturePointMask);
	}

	private static PreviewOverlayEdge[] CreateEdges()
	{
		(int, int)[] tessellationEdges = MediaPipeFaceMeshTopology.TessellationEdges;
		PreviewOverlayEdge[] array = new PreviewOverlayEdge[tessellationEdges.Length];
		for (int i = 0; i < tessellationEdges.Length; i++)
		{
			array[i] = new PreviewOverlayEdge(tessellationEdges[i].Item1, tessellationEdges[i].Item2);
		}
		return array;
	}

	private static PreviewOverlayIndexedPath[] CreateBaseFeaturePaths()
	{
		return new PreviewOverlayIndexedPath[8]
		{
			new PreviewOverlayIndexedPath(EyeA, Closed: true, PreviewOverlayMeshFeatureRole.Eye),
			new PreviewOverlayIndexedPath(EyeB, Closed: true, PreviewOverlayMeshFeatureRole.Eye),
			new PreviewOverlayIndexedPath(OuterLip, Closed: true, PreviewOverlayMeshFeatureRole.Mouth),
			new PreviewOverlayIndexedPath(InnerLip, Closed: true, PreviewOverlayMeshFeatureRole.Mouth),
			new PreviewOverlayIndexedPath(Jaw, Closed: false, PreviewOverlayMeshFeatureRole.Jaw),
			new PreviewOverlayIndexedPath(NoseBridge, Closed: false, PreviewOverlayMeshFeatureRole.Nose),
			new PreviewOverlayIndexedPath(NoseBase, Closed: false, PreviewOverlayMeshFeatureRole.Nose),
			new PreviewOverlayIndexedPath(MediaPipeFaceMeshTopology.FaceOvalIndices, Closed: true, PreviewOverlayMeshFeatureRole.Face)
		};
	}

	public static PreviewOverlayIndexedPath[] CreateFeaturePaths(
		IReadOnlyList<FaceMeshLandmarkPoint> denseMeshPoints)
	{
		IReadOnlyList<int> readOnlyList = MediaPipeBrowOutlineGeometry.BuildClosedOutlineIndices(denseMeshPoints, MediaPipeBrowOutlineGeometry.BrowAIndices);
		IReadOnlyList<int> readOnlyList2 = MediaPipeBrowOutlineGeometry.BuildClosedOutlineIndices(denseMeshPoints, MediaPipeBrowOutlineGeometry.BrowBIndices);
		int num = ((readOnlyList.Count >= 3) ? 1 : 0) + ((readOnlyList2.Count >= 3) ? 1 : 0);
		PreviewOverlayIndexedPath[] array = new PreviewOverlayIndexedPath[BaseFeaturePaths.Length + num];
		int destinationIndex = 0;
		array[destinationIndex++] = BaseFeaturePaths[0];
		array[destinationIndex++] = BaseFeaturePaths[1];
		if (readOnlyList.Count >= 3)
		{
			array[destinationIndex++] = new PreviewOverlayIndexedPath(readOnlyList, Closed: true, PreviewOverlayMeshFeatureRole.Brow);
		}
		if (readOnlyList2.Count >= 3)
		{
			array[destinationIndex++] = new PreviewOverlayIndexedPath(readOnlyList2, Closed: true, PreviewOverlayMeshFeatureRole.Brow);
		}
		Array.Copy(BaseFeaturePaths, 2, array, destinationIndex, BaseFeaturePaths.Length - 2);
		return array;
	}

	private static bool[] CreateFeaturePointMask()
	{
		bool[] array = new bool[468];
		PreviewOverlayIndexedPath[] baseFeaturePaths = BaseFeaturePaths;
		foreach (PreviewOverlayIndexedPath previewOverlayIndexedPath in baseFeaturePaths)
		{
			MarkFeaturePoints(array, previewOverlayIndexedPath.PointIndices);
		}
		MarkFeaturePoints(array, MediaPipeBrowOutlineGeometry.BrowAIndices);
		MarkFeaturePoints(array, MediaPipeBrowOutlineGeometry.BrowBIndices);
		return array;
	}

	private static void MarkFeaturePoints(bool[] mask, IReadOnlyList<int> indices)
	{
		foreach (int index in indices)
		{
			if ((uint)index < (uint)mask.Length)
			{
				mask[index] = true;
			}
		}
	}
}
