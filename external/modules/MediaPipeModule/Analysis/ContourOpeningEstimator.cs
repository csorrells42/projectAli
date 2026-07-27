using System;
using System.Collections.Generic;
using System.Windows;

namespace AvatarBuilder.Modules.Vision.Analysis;

public static class ContourOpeningEstimator
{
	private readonly record struct ContourAxis(double AxisX, double AxisY, double CrossX, double CrossY);

	public static double? CalculateOpeningRatio(IReadOnlyList<Point> contour)
	{
		return CalculateOpeningRatio(contour, preferPairedAverage: false);
	}

	public static double? CalculateOpeningRatio(IReadOnlyList<Point> contour, bool preferPairedAverage)
	{
		if (contour.Count < 4)
		{
			return null;
		}
		if (!preferPairedAverage)
		{
			return CalculateAxisOpeningRatio(contour) ?? CalculateAxisAlignedOpeningRatio(contour);
		}
		return CalculatePairedAverageOpeningRatio(contour) ?? CalculateAxisOpeningRatio(contour) ?? CalculateAxisAlignedOpeningRatio(contour);
	}

	public static double? CalculateAxisAlignedOpeningRatio(IReadOnlyList<Point> contour)
	{
		if (contour.Count < 4)
		{
			return null;
		}
		Point point = contour[0];
		double num = point.X;
		double num2 = point.X;
		double num3 = point.Y;
		double num4 = point.Y;
		for (int i = 1; i < contour.Count; i++)
		{
			Point point2 = contour[i];
			num = Math.Min(num, point2.X);
			num2 = Math.Max(num2, point2.X);
			num3 = Math.Min(num3, point2.Y);
			num4 = Math.Max(num4, point2.Y);
		}
		double num5 = num2 - num;
		double num6 = num4 - num3;
		if (!(num5 <= 0.0001) && !(num6 <= 0.0001))
		{
			return Math.Clamp(num6 / num5, 0.0, 2.0);
		}
		return null;
	}

	private static double? CalculatePairedAverageOpeningRatio(IReadOnlyList<Point> contour)
	{
		Point first = contour[0];
		int num = contour.Count / 2;
		Point opposite = contour[num];
		ContourAxis? contourAxis = CreateAxis(first, opposite);
		if (contourAxis.HasValue)
		{
			ContourAxis valueOrDefault = contourAxis.GetValueOrDefault();
			int num2 = contour.Count / 2;
			Span<double> span = ((num2 > 64) ? ((Span<double>)new double[num2]) : stackalloc double[num2]);
			Span<double> span2 = span;
			int num3 = 0;
			for (int i = 1; i < num; i++)
			{
				int num4 = contour.Count - i;
				if (num4 > i && num4 < contour.Count)
				{
					span2[num3++] = Math.Abs(ProjectAcross(contour[i], valueOrDefault) - ProjectAcross(contour[num4], valueOrDefault));
				}
			}
			if (num3 == 0)
			{
				return null;
			}
			double num5 = CalculateAxisWidth(contour, valueOrDefault);
			if (num5 <= 0.0001)
			{
				return null;
			}
			Span<double> span3 = span2.Slice(0, num3);
			span3.Sort();
			int num6 = ((num3 >= 5) ? 1 : 0);
			int num7 = num3 - num6 * 2;
			double num8 = 0.0;
			for (int j = num6; j < num3 - num6; j++)
			{
				num8 += span3[j];
			}
			return Math.Clamp(num8 / (double)num7 / num5, 0.0, 2.0);
		}
		return null;
	}

	private static double? CalculateAxisOpeningRatio(IReadOnlyList<Point> contour)
	{
		Point first = contour[0];
		Point opposite = contour[contour.Count / 2];
		ContourAxis? contourAxis = CreateAxis(first, opposite);
		if (contourAxis.HasValue)
		{
			ContourAxis valueOrDefault = contourAxis.GetValueOrDefault();
			double num = double.PositiveInfinity;
			double num2 = double.NegativeInfinity;
			double num3 = double.PositiveInfinity;
			double num4 = double.NegativeInfinity;
			foreach (Point item in contour)
			{
				double val = ProjectAlong(item, valueOrDefault);
				double val2 = ProjectAcross(item, valueOrDefault);
				num = Math.Min(num, val);
				num2 = Math.Max(num2, val);
				num3 = Math.Min(num3, val2);
				num4 = Math.Max(num4, val2);
			}
			double num5 = num2 - num;
			double num6 = num4 - num3;
			if (!(num5 <= 0.0001) && !(num6 <= 0.0001))
			{
				return Math.Clamp(num6 / num5, 0.0, 2.0);
			}
			return null;
		}
		return null;
	}

	private static double CalculateAxisWidth(IReadOnlyList<Point> contour, ContourAxis axis)
	{
		double num = double.PositiveInfinity;
		double num2 = double.NegativeInfinity;
		foreach (Point item in contour)
		{
			double val = ProjectAlong(item, axis);
			num = Math.Min(num, val);
			num2 = Math.Max(num2, val);
		}
		return num2 - num;
	}

	private static ContourAxis? CreateAxis(Point first, Point opposite)
	{
		double num = opposite.X - first.X;
		double num2 = opposite.Y - first.Y;
		double num3 = Math.Sqrt(num * num + num2 * num2);
		if (num3 <= 0.0001)
		{
			return null;
		}
		num /= num3;
		num2 /= num3;
		return new ContourAxis(num, num2, 0.0 - num2, num);
	}

	private static double ProjectAlong(Point point, ContourAxis axis)
	{
		return point.X * axis.AxisX + point.Y * axis.AxisY;
	}

	private static double ProjectAcross(Point point, ContourAxis axis)
	{
		return point.X * axis.CrossX + point.Y * axis.CrossY;
	}
}
