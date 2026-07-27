using System;

namespace AvatarBuilder.Modules.Webcam.Common;

public readonly record struct VideoFrameColorSettings(bool Enabled, double Exposure, double Contrast, double Saturation, double Warmth)
{
	public static VideoFrameColorSettings Off { get; } = new VideoFrameColorSettings(Enabled: false, 0.0, 0.0, 0.0, 0.0);

	public bool HasVisibleAdjustments
	{
		get
		{
			if (Enabled)
			{
				if (!(Math.Abs(Exposure) > 0.001) && !(Math.Abs(Contrast) > 0.001) && !(Math.Abs(Saturation) > 0.001))
				{
					return Math.Abs(Warmth) > 0.001;
				}
				return true;
			}
			return false;
		}
	}
}
