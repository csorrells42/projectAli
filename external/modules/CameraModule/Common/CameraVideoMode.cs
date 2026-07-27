namespace AvatarBuilder.Modules.Webcam.Common;

public sealed class CameraVideoMode
{
	public static CameraVideoMode Auto { get; } = new CameraVideoMode("Auto", null, null, null, null, isAuto: true);

	public string Label { get; }

	public int? Width { get; }

	public int? Height { get; }

	public double? FramesPerSecond { get; }

	public string? InputFormat { get; }

	public bool IsAuto { get; }

	public CameraVideoMode(string label, int? width, int? height, double? framesPerSecond, string? inputFormat, bool isAuto = false)
	{
		Label = label;
		Width = width;
		Height = height;
		FramesPerSecond = framesPerSecond;
		InputFormat = inputFormat;
		IsAuto = isAuto;
	}

	public override string ToString()
	{
		return Label;
	}
}
