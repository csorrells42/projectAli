using System;
using System.IO;

namespace AvatarBuilder.Modules.Vision.Identity;

public sealed class FaceIdentityModelInfo
{
	private const string RelativeModelDirectory =
		"dependencies/vision/opencv/sface";

	private const string ModelFileName =
		"face_recognition_sface_2021dec.onnx";

	public string ModelPath { get; init; } = "";

	public string LicensePath { get; init; } = "";

	public bool IsReady =>
		File.Exists(ModelPath) &&
		File.Exists(LicensePath);

	public string Status => IsReady
		? "OpenCV SFace identity model ready"
		: "OpenCV SFace identity model or license missing";

	public static FaceIdentityModelInfo Load()
	{
		string directory = Path.Combine(
			AppContext.BaseDirectory,
			RelativeModelDirectory.Replace(
				'/',
				Path.DirectorySeparatorChar));
		return new FaceIdentityModelInfo
		{
			ModelPath = Path.Combine(directory, ModelFileName),
			LicensePath = Path.Combine(directory, "LICENSE")
		};
	}
}
