using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AvatarBuilder.Modules.Vision.Common;

public interface IFaceLandmarkCropRefiner
{
	bool IsAvailable { get; }

	FaceLandmarkTrackingResult DetectFaceCrop(BitmapSource bitmap, Rect normalizedFaceHint, DateTime capturedAtUtc);
}
