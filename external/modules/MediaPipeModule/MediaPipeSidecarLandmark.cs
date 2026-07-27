using System.Text.Json.Serialization;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

internal readonly struct MediaPipeSidecarLandmark
{
	public MediaPipeSidecarLandmark(double x, double y, double z)
	{
		X = x;
		Y = y;
		Z = z;
	}

	[JsonPropertyName("x")]
	public double X { get; init; }

	[JsonPropertyName("y")]
	public double Y { get; init; }

	[JsonPropertyName("z")]
	public double Z { get; init; }
}
