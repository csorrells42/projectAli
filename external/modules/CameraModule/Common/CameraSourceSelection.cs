using System;
using System.Collections.Generic;
using System.Linq;
using AvatarBuilder.Modules.Webcam.DirectShow;
using AvatarBuilder.Modules.Webcam.MediaFoundation;

namespace AvatarBuilder.Modules.Webcam.Common;

public static class CameraSourceSelection
{
	public static IReadOnlyList<CameraDevice> GetCameras()
	{
		return CameraDeviceCatalog.MergeDevices(MediaFoundationCameraEnumerator.GetVideoInputDevices(), DirectShowCameraEnumerator.GetVideoInputDevices());
	}

	public static CameraDevice? GetDefaultCamera()
	{
		return GetCameras().FirstOrDefault();
	}

	public static CameraDevice RequireDefaultCamera()
	{
		return GetDefaultCamera() ?? throw new InvalidOperationException("No camera devices were found.");
	}
}
