using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Webcam.Common;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

public static class MediaFoundationCameraEnumerator
{
	public static IReadOnlyList<CameraDevice> GetVideoInputDevices()
	{
		if (!OperatingSystem.IsWindows())
		{
			return Array.Empty<CameraDevice>();
		}
		using (MediaFoundationCameraDeviceFactory.Startup())
		{
			IReadOnlyList<IMFActivate> readOnlyList = MediaFoundationCameraDeviceFactory.EnumerateVideoActivates();
			List<CameraDevice> list = new List<CameraDevice>();
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (IMFActivate item2 in readOnlyList)
			{
				try
				{
				string? allocatedString = MediaFoundationInterop.GetAllocatedString(item2, MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
				string? allocatedString2 = MediaFoundationInterop.GetAllocatedString(item2, MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
					if (!string.IsNullOrWhiteSpace(allocatedString))
					{
						string item = (string.IsNullOrWhiteSpace(allocatedString2) ? allocatedString : allocatedString2);
						if (hashSet.Add(item))
						{
							list.Add(new CameraDevice(list.Count, allocatedString, allocatedString2 ?? string.Empty, "Media Foundation"));
						}
					}
				}
				finally
				{
					MediaFoundationInterop.ReleaseComObject(item2);
				}
			}
			return list;
		}
	}
}
