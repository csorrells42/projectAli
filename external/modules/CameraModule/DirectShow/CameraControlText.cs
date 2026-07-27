using System;
using AvatarBuilder.Modules.Webcam.Common;

namespace AvatarBuilder.Modules.Webcam.DirectShow;

internal static class CameraControlText
{
	internal static string FormatChooseCameraControlsStatus()
	{
		return "Choose a camera to load controls.";
	}

	internal static string FormatNoCameraControlsStatus()
	{
		return "No standard Windows camera controls were exposed by this source.";
	}

	internal static string FormatCameraControlsLoadedStatus(CameraDevice camera, int controlCount)
	{
		return $"{controlCount} Windows camera controls exposed by {camera.Name}.";
	}

	internal static string FormatCameraControlSetStatus(CameraControlItem control, int value, bool isAuto, bool success)
	{
		if (!success)
		{
			return "Could not set " + control.Name + ". The camera may be busy or this control may be locked by its driver.";
		}
		return control.Name + ": " + (isAuto ? "Auto" : FormatCameraControlValue(value));
	}

	internal static int RoundCameraControlToStep(double value, CameraControlItem control)
	{
		int num = Math.Max(1, control.Step);
		return Math.Clamp(control.Minimum + (int)Math.Round((value - (double)control.Minimum) / (double)num) * num, control.Minimum, control.Maximum);
	}

	internal static int ApplyCameraControlDefaultMagnet(int value, CameraControlItem control)
	{
		double num = Math.Max((double)control.Step * 1.25, (double)(control.Maximum - control.Minimum) * 0.025);
		if (!((double)Math.Abs(value - control.DefaultValue) <= num))
		{
			return value;
		}
		return control.DefaultValue;
	}

	internal static string FormatCameraControlValue(int value)
	{
		return value.ToString("0");
	}
}
