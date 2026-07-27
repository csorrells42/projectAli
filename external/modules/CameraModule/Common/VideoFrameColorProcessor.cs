using System;

namespace AvatarBuilder.Modules.Webcam.Common;

public static class VideoFrameColorProcessor
{
	public static void Apply(byte[] bgraBytes, VideoFrameColorSettings settings)
	{
		if (settings.HasVisibleAdjustments)
		{
			double num = Math.Clamp(settings.Exposure, -30.0, 30.0) * 2.2;
			double contrast = 1.0 + Math.Clamp(settings.Contrast, -40.0, 40.0) / 100.0;
			double num2 = 1.0 + Math.Clamp(settings.Saturation, -40.0, 40.0) / 100.0;
			double num3 = Math.Clamp(settings.Warmth, -40.0, 40.0) * 0.9;
			for (int i = 0; i + 3 < bgraBytes.Length; i += 4)
			{
				byte num4 = bgraBytes[i];
				byte num5 = bgraBytes[i + 1];
				double num6 = ApplyContrast((double)(int)bgraBytes[i + 2] + num + num3, contrast);
				double num7 = ApplyContrast((double)(int)num5 + num, contrast);
				double num8 = ApplyContrast((double)(int)num4 + num - num3, contrast);
				double num9 = num6 * 0.2126 + num7 * 0.7152 + num8 * 0.0722;
				num6 = num9 + (num6 - num9) * num2;
				num7 = num9 + (num7 - num9) * num2;
				num8 = num9 + (num8 - num9) * num2;
				bgraBytes[i] = ClampByte(num8);
				bgraBytes[i + 1] = ClampByte(num7);
				bgraBytes[i + 2] = ClampByte(num6);
				bgraBytes[i + 3] = byte.MaxValue;
			}
		}
	}

	private static double ApplyContrast(double value, double contrast)
	{
		return (value - 128.0) * contrast + 128.0;
	}

	private static byte ClampByte(double value)
	{
		return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
	}
}
