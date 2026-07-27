using System;

namespace AvatarBuilder.Modules.Viewports.Contracts;

public readonly record struct PreviewOverlayRect(double Left, double Top, double Right, double Bottom)
{
	public PreviewOverlayRect Clamp()
	{
		double left = Math.Clamp(Math.Min(Left, Right), 0.0, 1.0);
		double top = Math.Clamp(Math.Min(Top, Bottom), 0.0, 1.0);
		double right = Math.Clamp(Math.Max(Left, Right), 0.0, 1.0);
		double bottom = Math.Clamp(Math.Max(Top, Bottom), 0.0, 1.0);
		return new PreviewOverlayRect(left, top, right, bottom);
	}
}
