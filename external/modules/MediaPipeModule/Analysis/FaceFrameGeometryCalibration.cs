namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceFrameGeometryCalibration
{
	public static FaceFrameGeometryCalibration None { get; } = new FaceFrameGeometryCalibration();

	public double? ReferenceDistanceInches { get; init; }

	public double? ReferenceInterEyeFrameWidth { get; init; }

	public int ReferenceSampleCount { get; init; }

	public string ReferenceSource { get; init; } = "";

	public double? CameraHorizontalFovDegrees { get; init; }

	public double? InterpupillaryDistanceInches { get; init; }

	public bool HasDistanceReference
	{
		get
		{
			double? referenceDistanceInches = ReferenceDistanceInches;
			if (referenceDistanceInches.HasValue && referenceDistanceInches.GetValueOrDefault() > 0.0)
			{
				referenceDistanceInches = ReferenceInterEyeFrameWidth;
				if (referenceDistanceInches.HasValue)
				{
					return referenceDistanceInches.GetValueOrDefault() > 0.0;
				}
				return false;
			}
			return false;
		}
	}

	public bool HasApparentReference
	{
		get
		{
			double? referenceInterEyeFrameWidth = ReferenceInterEyeFrameWidth;
			if (referenceInterEyeFrameWidth.HasValue)
			{
				return referenceInterEyeFrameWidth.GetValueOrDefault() > 0.0;
			}
			return false;
		}
	}

	public bool HasCameraIntrinsics
	{
		get
		{
			double? cameraHorizontalFovDegrees = CameraHorizontalFovDegrees;
			if (cameraHorizontalFovDegrees.HasValue)
			{
				double valueOrDefault = cameraHorizontalFovDegrees.GetValueOrDefault();
				if (valueOrDefault > 0.0 && valueOrDefault < 180.0)
				{
					cameraHorizontalFovDegrees = InterpupillaryDistanceInches;
					if (cameraHorizontalFovDegrees.HasValue)
					{
						return cameraHorizontalFovDegrees.GetValueOrDefault() > 0.0;
					}
					return false;
				}
			}
			return false;
		}
	}
}
