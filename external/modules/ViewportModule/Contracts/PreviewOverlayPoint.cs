using System;

namespace AvatarBuilder.Modules.Viewports.Contracts;

public readonly record struct PreviewOverlayPoint(double X, double Y)
{
	public PreviewOverlayPoint Clamp()
	{
		return new PreviewOverlayPoint(Math.Clamp(X, 0.0, 1.0), Math.Clamp(Y, 0.0, 1.0));
	}
}
