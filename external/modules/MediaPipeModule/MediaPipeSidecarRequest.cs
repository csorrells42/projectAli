using System.Text.Json.Serialization;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

internal sealed class MediaPipeSidecarRequest
{
	[JsonPropertyName("requestId")]
	public int RequestId { get; init; }

	[JsonPropertyName("sharedMemoryName")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? SharedMemoryName { get; init; }

	[JsonPropertyName("sharedMemoryCapacityBytes")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public long SharedMemoryCapacityBytes { get; init; }

	[JsonPropertyName("imageByteLength")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public int ImageByteLength { get; init; }

	[JsonPropertyName("imageWidth")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public int ImageWidth { get; init; }

	[JsonPropertyName("imageHeight")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public int ImageHeight { get; init; }

	[JsonPropertyName("imageStride")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public int ImageStride { get; init; }

	[JsonPropertyName("imagePixelFormat")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? ImagePixelFormat { get; init; }

	[JsonPropertyName("landmarkSharedMemoryName")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? LandmarkSharedMemoryName { get; init; }

	[JsonPropertyName("landmarkSharedMemoryCapacityBytes")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public int LandmarkSharedMemoryCapacityBytes { get; init; }

	[JsonPropertyName("timestampMilliseconds")]
	public long TimestampMilliseconds { get; init; }

	[JsonPropertyName("collectDiagnostics")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool CollectDiagnostics { get; init; }
}
