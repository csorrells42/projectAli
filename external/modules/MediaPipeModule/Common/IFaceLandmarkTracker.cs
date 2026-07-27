using System;
using System.Windows.Media.Imaging;

namespace AvatarBuilder.Modules.Vision.Common;

public interface IFaceLandmarkTracker : IDisposable
{
	string Name { get; }

	bool IsAvailable { get; }

	int MaxDetectionDimension { get; set; }

	FaceLandmarkTrackingResult Detect(BitmapSource bitmap, DateTime capturedAtUtc);
}
