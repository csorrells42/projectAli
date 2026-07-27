using System;
using System.IO;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed class OpenCvYuNetModelInfo
{
	private const string RelativeModelDirectory = "dependencies/vision/opencv/yunet";

	private const string DefaultModelFileName = "face_detection_yunet_2023mar.onnx";

	public string ModelDirectory { get; init; } = "";

	public string ModelPath { get; init; } = "";

	public bool ModelExists => File.Exists(ModelPath);

	public bool IsReady => ModelExists;

	public string Status
	{
		get
		{
			if (!ModelExists)
			{
				return "OpenCV YuNet face detector model missing";
			}
			return "OpenCV YuNet face detector model ready";
		}
	}

	public static OpenCvYuNetModelInfo Load()
	{
		string text = Path.Combine(AppContext.BaseDirectory, "dependencies/vision/opencv/yunet".Replace('/', Path.DirectorySeparatorChar));
		return new OpenCvYuNetModelInfo
		{
			ModelDirectory = text,
			ModelPath = Path.Combine(text, "face_detection_yunet_2023mar.onnx")
		};
	}
}
