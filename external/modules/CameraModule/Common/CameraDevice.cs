using System;
using System.Collections.Generic;
using System.Linq;

namespace AvatarBuilder.Modules.Webcam.Common;

public sealed class CameraDevice
{
	public int DeviceNumber { get; }

	public string Name { get; }

	public string DevicePath { get; }

	public string Source { get; }

	public CameraDevice? FallbackDevice { get; }

	public bool HasFallbackDevice => FallbackDevice != null;

	public string DisplayName
	{
		get
		{
			if (!HasFallbackDevice && !string.IsNullOrWhiteSpace(Source))
			{
				return Name + " (" + Source + ")";
			}
			return Name;
		}
	}

	public CameraDevice(int deviceNumber, string name, string devicePath, string source = "", CameraDevice? fallbackDevice = null)
	{
		DeviceNumber = deviceNumber;
		Name = name;
		DevicePath = devicePath;
		Source = source;
		FallbackDevice = fallbackDevice;
	}

	public CameraDevice WithFallback(CameraDevice fallbackDevice)
	{
		return new CameraDevice(DeviceNumber, Name, DevicePath, Source, fallbackDevice);
	}

	public IEnumerable<CameraDevice> EnumerateSourceDevices()
	{
		yield return this;
		if (FallbackDevice != null)
		{
			yield return FallbackDevice;
		}
	}

	public CameraDevice DirectShowDeviceOrSelf()
	{
		return EnumerateSourceDevices().FirstOrDefault((CameraDevice device) => string.Equals(device.Source, "DirectShow", StringComparison.OrdinalIgnoreCase)) ?? this;
	}

	public override string ToString()
	{
		return DisplayName;
	}
}
