using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using AvatarBuilder.Modules.Vision.Diagnostics;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

internal sealed class MediaPipeSidecarResponse
{
	private static readonly IReadOnlyDictionary<string, double> EmptyTimings = new Dictionary<string, double>();

	[JsonPropertyName("requestId")]
	public int RequestId { get; init; }

	[JsonPropertyName("ok")]
	public bool Ok { get; init; }

	[JsonPropertyName("hasFace")]
	public bool HasFace { get; init; }

	[JsonPropertyName("status")]
	public string Status { get; set; } = "";

	[JsonIgnore]
	public IReadOnlyList<MediaPipeSidecarLandmark> Landmarks { get; set; } = Array.Empty<MediaPipeSidecarLandmark>();

	[JsonPropertyName("landmarkCount")]
	public int LandmarkCount { get; init; }

	[JsonPropertyName("eyeBlinkLeft")]
	public double? EyeBlinkLeftScore { get; init; }

	[JsonPropertyName("eyeBlinkRight")]
	public double? EyeBlinkRightScore { get; init; }

	[JsonPropertyName("jawOpen")]
	public double? JawOpenScore { get; init; }

	[JsonPropertyName("mouthClose")]
	public double? MouthCloseScore { get; init; }

	[JsonPropertyName("faceBoxX")]
	public double? FaceBoxX { get; init; }

	[JsonPropertyName("faceBoxY")]
	public double? FaceBoxY { get; init; }

	[JsonPropertyName("faceBoxWidth")]
	public double? FaceBoxWidth { get; init; }

	[JsonPropertyName("faceBoxHeight")]
	public double? FaceBoxHeight { get; init; }

	[JsonPropertyName("faceDetectionScore")]
	public double? FaceDetectionScore { get; init; }

	[JsonPropertyName("facialTransformationMatrixCount")]
	public int FacialTransformationMatrixCount { get; init; }

	[JsonIgnore]
	public IReadOnlyList<double> FacialTransformationMatrix { get; set; } = Array.Empty<double>();

	[JsonPropertyName("timingsMilliseconds")]
	public IReadOnlyDictionary<string, double> TimingsMilliseconds { get; init; } = EmptyTimings;

	[JsonIgnore]
	public VisionPipelineDiagnostics Diagnostics { get; set; } = VisionPipelineDiagnostics.None;
}
