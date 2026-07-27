using System;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

public static class MediaPipeFaceGeometryEstimatorSelfTest
{
	public static MediaPipeFaceGeometryEstimatorSelfTestResult Run()
	{
		try
		{
			DenseFaceLandmarkModelInfo model =
				DenseFaceLandmarkModelInfo.Load();
			if (!model.ModelExists)
			{
				return new MediaPipeFaceGeometryEstimatorSelfTestResult(
					false,
					model.Status);
			}
			MediaPipeFaceGeometryEstimator estimator =
				MediaPipeFaceGeometryEstimator.Load(model.ModelPath);
			bool succeeded = estimator.RunDeterministicSelfTest(
				out double maximumError);
			return new MediaPipeFaceGeometryEstimatorSelfTestResult(
				succeeded,
				succeeded
					? $"MediaPipe face geometry self-test passed: " +
						$"33 canonical anchors, maximum matrix error " +
						$"{maximumError:0.000000000}."
					: $"MediaPipe face geometry self-test failed: " +
						$"maximum matrix error {maximumError:0.000000000}.");
		}
		catch (Exception ex)
		{
			return new MediaPipeFaceGeometryEstimatorSelfTestResult(
				false,
				"MediaPipe face geometry self-test failed: " + ex.Message);
		}
	}
}
