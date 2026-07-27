using System;
using System.Windows;

namespace AvatarBuilder.Modules.Vision.Common;

public sealed class FaceCueGuideLayout
{
	public double CenterXPercent { get; }

	public double CenterYPercent { get; }

	public double HeightPercent { get; }

	public FaceCueRelativeRegion Face => new FaceCueRelativeRegion(0.0, 0.0, 1.0, 1.0);

	public FaceCueRelativeRegion LeftEye => new FaceCueRelativeRegion(0.24, 0.24, 0.5, 0.45);

	public FaceCueRelativeRegion RightEye => new FaceCueRelativeRegion(0.5, 0.24, 0.76, 0.45);

	public FaceCueRelativeRegion Eyes => new FaceCueRelativeRegion(0.24, 0.24, 0.76, 0.45);

	public FaceCueRelativeRegion Jaw => new FaceCueRelativeRegion(0.25, 0.55, 0.75, 0.84);

	public FaceCueRelativeRegion LeftJaw => new FaceCueRelativeRegion(0.25, 0.55, 0.5, 0.84);

	public FaceCueRelativeRegion RightJaw => new FaceCueRelativeRegion(0.5, 0.55, 0.75, 0.84);

	public FaceCueGuideLayout(double centerXPercent, double centerYPercent, double heightPercent)
	{
		CenterXPercent = Math.Clamp(centerXPercent, 20.0, 80.0);
		CenterYPercent = Math.Clamp(centerYPercent, 20.0, 80.0);
		HeightPercent = Math.Clamp(heightPercent, 25.0, 90.0);
	}

	public Rect GetFaceBox()
	{
		double num = HeightPercent / 100.0;
		double num2 = num * 0.8;
		double num3 = CenterXPercent / 100.0;
		double num4 = CenterYPercent / 100.0;
		double x = Math.Clamp(num3 - num2 / 2.0, 0.0, 1.0 - num2);
		double y = Math.Clamp(num4 - num / 2.0, 0.0, 1.0 - num);
		return new Rect(x, y, num2, num);
	}

	public Rect ToFrameRect(FaceCueRelativeRegion region)
	{
		Rect faceBox = GetFaceBox();
		return new Rect(faceBox.X + faceBox.Width * region.Left, faceBox.Y + faceBox.Height * region.Top, faceBox.Width * (region.Right - region.Left), faceBox.Height * (region.Bottom - region.Top));
	}

	public Int32Rect ToPixelRegion(int width, int height, FaceCueRelativeRegion region)
	{
		Rect rect = ToFrameRect(region);
		int num = Math.Clamp((int)((double)width * rect.X), 0, width - 1);
		int num2 = Math.Clamp((int)((double)height * rect.Y), 0, height - 1);
		int width2 = Math.Clamp((int)((double)width * rect.Width), 1, width - num);
		int height2 = Math.Clamp((int)((double)height * rect.Height), 1, height - num2);
		return new Int32Rect(num, num2, width2, height2);
	}
}
