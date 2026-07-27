using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Webcam.Common;

public static class CameraDeviceCatalog
{
	public static IReadOnlyList<CameraDevice> MergeDevices(IReadOnlyList<CameraDevice> mediaFoundationDevices, IReadOnlyList<CameraDevice> directShowDevices)
	{
		List<CameraDevice> list = new List<CameraDevice>();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (CameraDevice mediaFoundationDevice in mediaFoundationDevices)
		{
			if (hashSet.Add(CreateExactKey(mediaFoundationDevice)))
			{
				list.Add(mediaFoundationDevice);
			string? text = TryCreatePhysicalDeviceKey(mediaFoundationDevice);
				if (!string.IsNullOrWhiteSpace(text))
				{
					dictionary.TryAdd(text, list.Count - 1);
				}
			}
		}
		foreach (CameraDevice directShowDevice in directShowDevices)
		{
			if (!hashSet.Add(CreateExactKey(directShowDevice)))
			{
				continue;
			}
			string? text2 = TryCreatePhysicalDeviceKey(directShowDevice);
			if (!string.IsNullOrWhiteSpace(text2) && dictionary.TryGetValue(text2, out var value))
			{
				CameraDevice cameraDevice = list[value];
				if (cameraDevice.FallbackDevice == null)
				{
					list[value] = cameraDevice.WithFallback(directShowDevice);
				}
			}
			else
			{
				list.Add(directShowDevice);
			}
		}
		return list;
	}

	public static string? TryCreatePhysicalDeviceKey(CameraDevice camera)
	{
		string text = camera.DevicePath.Trim();
		if (string.IsNullOrWhiteSpace(text) || text.StartsWith("@device:sw:", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		string text2 = text.Replace('/', '\\').ToLowerInvariant();
		int num = text2.IndexOf("#{", StringComparison.Ordinal);
		if (num > 0)
		{
			text2 = text2.Substring(0, num);
		}
		if (!text2.Contains('#', StringComparison.Ordinal))
		{
			return null;
		}
		return text2;
	}

	private static string CreateExactKey(CameraDevice camera)
	{
		if (!string.IsNullOrWhiteSpace(camera.DevicePath))
		{
			return "path:" + camera.DevicePath + "|source:" + camera.Source;
		}
		return "name:" + camera.Name + "|source:" + camera.Source;
	}
}
