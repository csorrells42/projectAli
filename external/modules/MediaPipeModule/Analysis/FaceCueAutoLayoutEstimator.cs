using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Analysis;

public static class FaceCueAutoLayoutEstimator
{
	public static FaceCueGuideLayout Estimate(BitmapSource bitmap, FaceCueGuideLayout current)
	{
		int width;
		int height;
		int stride;
		byte[] pixels = CreateGrayPixels(bitmap, out width, out height, out stride);
		FaceCueGuideLayout faceCueGuideLayout = current;
		double num = ScoreLayout(pixels, stride, width, height, current, current);
		foreach (FaceCueGuideLayout item in CreateCandidates(current))
		{
			double num2 = ScoreLayout(pixels, stride, width, height, item, current);
			if (num2 > num)
			{
				num = num2;
				faceCueGuideLayout = item;
			}
		}
		return new FaceCueGuideLayout(Lerp(current.CenterXPercent, faceCueGuideLayout.CenterXPercent, 0.28), Lerp(current.CenterYPercent, faceCueGuideLayout.CenterYPercent, 0.28), Lerp(current.HeightPercent, faceCueGuideLayout.HeightPercent, 0.18));
	}

	private static IEnumerable<FaceCueGuideLayout> CreateCandidates(FaceCueGuideLayout current)
	{
		for (double dx = -12.0; dx <= 12.0; dx += 3.0)
		{
			for (double dy = -12.0; dy <= 12.0; dy += 3.0)
			{
				for (double ds = -10.0; ds <= 10.0; ds += 5.0)
				{
					yield return new FaceCueGuideLayout(current.CenterXPercent + dx, current.CenterYPercent + dy, current.HeightPercent + ds);
				}
			}
		}
	}

	private static double ScoreLayout(byte[] pixels, int stride, int width, int height, FaceCueGuideLayout candidate, FaceCueGuideLayout current)
	{
		Int32Rect region = candidate.ToPixelRegion(width, height, candidate.Face);
		Int32Rect region2 = candidate.ToPixelRegion(width, height, candidate.Eyes);
		Int32Rect region3 = candidate.ToPixelRegion(width, height, candidate.Jaw);
		double num = CalculateQuality(pixels, stride, region);
		double num2 = CalculateVerticalContrast(pixels, stride, region2);
		double num3 = CalculateEdgeAndDarknessScore(pixels, stride, region3);
		double num4 = Math.Abs(candidate.CenterXPercent - current.CenterXPercent) * 0.012 + Math.Abs(candidate.CenterYPercent - current.CenterYPercent) * 0.012 + Math.Abs(candidate.HeightPercent - current.HeightPercent) * 0.008;
		return num * 0.42 + num2 * 0.34 + num3 * 0.24 - num4;
	}

	private static byte[] CreateGrayPixels(BitmapSource bitmap, out int width, out int height, out int stride)
	{
		double num = Math.Min(1.0, 120.0 / (double)Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
		FormatConvertedBitmap formatConvertedBitmap = new FormatConvertedBitmap(new TransformedBitmap(bitmap, new ScaleTransform(num, num)), PixelFormats.Gray8, null, 0.0);
		width = formatConvertedBitmap.PixelWidth;
		height = formatConvertedBitmap.PixelHeight;
		stride = Math.Max(1, (width * formatConvertedBitmap.Format.BitsPerPixel + 7) / 8);
		byte[] array = new byte[stride * height];
		formatConvertedBitmap.CopyPixels(array, stride, 0);
		return array;
	}

	private static double CalculateQuality(byte[] pixels, int stride, Int32Rect region)
	{
		double num = 0.0;
		double num2 = 0.0;
		int num3 = 0;
		for (int i = region.Y; i < region.Y + region.Height; i++)
		{
			int num4 = i * stride;
			for (int j = region.X; j < region.X + region.Width; j++)
			{
				double num5 = (double)(int)pixels[num4 + j] / 255.0;
				num += num5;
				num2 += num5 * num5;
				num3++;
			}
		}
		if (num3 == 0)
		{
			return 0.0;
		}
		double num6 = num / (double)num3;
		double d = Math.Max(0.0, num2 / (double)num3 - num6 * num6);
		double num7 = 1.0 - Math.Clamp(Math.Abs(num6 - 0.52) / 0.52, 0.0, 1.0);
		double num8 = Math.Clamp(Math.Sqrt(d) / 0.22, 0.0, 1.0);
		return num7 * 0.55 + num8 * 0.45;
	}

	private static double CalculateVerticalContrast(byte[] pixels, int stride, Int32Rect region)
	{
		long num = 0L;
		int num2 = 0;
		for (int i = region.Y + 1; i < region.Y + region.Height; i++)
		{
			int num3 = i * stride;
			int num4 = (i - 1) * stride;
			for (int j = region.X; j < region.X + region.Width; j++)
			{
				num += Math.Abs(pixels[num3 + j] - pixels[num4 + j]);
				num2++;
			}
		}
		if (num2 != 0)
		{
			return (double)num / ((double)num2 * 255.0);
		}
		return 0.0;
	}

	private static double CalculateEdgeAndDarknessScore(byte[] pixels, int stride, Int32Rect region)
	{
		long num = 0L;
		long num2 = 0L;
		int num3 = 0;
		for (int i = region.Y + 1; i < region.Y + region.Height; i++)
		{
			int num4 = i * stride;
			int num5 = (i - 1) * stride;
			for (int j = region.X + 1; j < region.X + region.Width; j++)
			{
				byte b = pixels[num4 + j];
				num += Math.Abs(b - pixels[num4 + j - 1]);
				num += Math.Abs(b - pixels[num5 + j]);
				num2 += 255 - b;
				num3++;
			}
		}
		if (num3 == 0)
		{
			return 0.0;
		}
		double num6 = (double)num / ((double)num3 * 510.0);
		double num7 = (double)num2 / ((double)num3 * 255.0);
		return num6 * 0.65 + num7 * 0.35;
	}

	private static double Lerp(double from, double to, double amount)
	{
		return from + (to - from) * amount;
	}
}
