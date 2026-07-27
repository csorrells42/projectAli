using System;
using System.Collections.Generic;
using System.Windows;

namespace AvatarBuilder.Modules.Vision.Common;

/// <summary>
/// Exact oriented image region consumed by a face tracker for one frame.
/// Coordinates are normalized to the source frame. Width and height describe
/// the same square pixel crop on the frame's horizontal and vertical axes.
/// </summary>
public readonly record struct FaceTrackingRegion(
	double CenterX,
	double CenterY,
	double Width,
	double Height,
	double RotationRadians)
{
	public bool IsValid =>
		double.IsFinite(CenterX)
		&& double.IsFinite(CenterY)
		&& double.IsFinite(Width)
		&& double.IsFinite(Height)
		&& double.IsFinite(RotationRadians)
		&& Width > 0d
		&& Height > 0d;

	public IReadOnlyList<Point> GetCorners()
	{
		if (!IsValid)
		{
			return Array.Empty<Point>();
		}

		double halfWidth = Width * 0.5d;
		double halfHeight = Height * 0.5d;
		double cosine = Math.Cos(RotationRadians);
		double sine = Math.Sin(RotationRadians);
		double centerX = CenterX;
		double centerY = CenterY;
		return
		[
			Transform(-1d, -1d),
			Transform(1d, -1d),
			Transform(1d, 1d),
			Transform(-1d, 1d)
		];

		Point Transform(double rightSign, double downSign)
		{
			return new Point(
				centerX
					+ (cosine * rightSign - sine * downSign) * halfWidth,
				centerY
					+ (sine * rightSign + cosine * downSign) * halfHeight);
		}
	}
}
