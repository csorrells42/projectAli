using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using AvatarBuilder.Modules.Webcam.Common;

namespace AvatarBuilder.Modules.Webcam.DirectShow;

public static class DirectShowCameraEnumerator
{
	[ComImport]
	[Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface ICreateDevEnum
	{
		[PreserveSig]
		int CreateClassEnumerator(ref Guid deviceClass, out IEnumMoniker? enumMoniker, int flags);
	}

	[ComImport]
	[Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPropertyBag
	{
		void Read([MarshalAs(UnmanagedType.LPWStr)] string propertyName, [MarshalAs(UnmanagedType.Struct)] out object? value, nint errorLog);

		void Write([MarshalAs(UnmanagedType.LPWStr)] string propertyName, [MarshalAs(UnmanagedType.Struct)] ref object value);
	}

	private static readonly Guid SystemDeviceEnumClsid = new Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");

	private static readonly Guid VideoInputDeviceCategory = new Guid("860BB310-5D01-11d0-BD3B-00A0C911CE86");

	public static IReadOnlyList<CameraDevice> GetVideoInputDevices()
	{
		List<CameraDevice> list = new List<CameraDevice>();
		object? obj = null;
		IEnumMoniker? enumMoniker = null;
		try
		{
			Type deviceEnumeratorType = Type.GetTypeFromCLSID(SystemDeviceEnumClsid, throwOnError: true)
				?? throw new InvalidOperationException("DirectShow system device enumerator type is unavailable.");
			obj = Activator.CreateInstance(deviceEnumeratorType);
			if (!(obj is ICreateDevEnum createDevEnum))
			{
				return list;
			}
			Guid deviceClass = VideoInputDeviceCategory;
			if (createDevEnum.CreateClassEnumerator(ref deviceClass, out enumMoniker, 0) != 0 || enumMoniker == null)
			{
				return list;
			}
			IMoniker[] array = new IMoniker[1];
			int num = 0;
			while (enumMoniker.Next(1, array, IntPtr.Zero) == 0)
			{
				IMoniker moniker = array[0];
				try
				{
					string text = ReadProperty(moniker, "FriendlyName") ?? "Camera";
					string devicePath = ReadProperty(moniker, "DevicePath") ?? GetDisplayName(moniker) ?? text;
					list.Add(new CameraDevice(num, text, devicePath, "DirectShow"));
					num++;
				}
				finally
				{
					Marshal.ReleaseComObject(moniker);
				}
			}
			return list;
		}
		catch (Exception)
		{
			return list;
		}
		finally
		{
			if (enumMoniker != null)
			{
				Marshal.ReleaseComObject(enumMoniker);
			}
			if (obj != null)
			{
				Marshal.ReleaseComObject(obj);
			}
		}
	}

	private static string? ReadProperty(IMoniker moniker, string propertyName)
	{
				object? ppvObj = null;
		try
		{
			Guid riid = typeof(IPropertyBag).GUID;
					moniker.BindToStorage(null!, null!, ref riid, out ppvObj);
			if (!(ppvObj is IPropertyBag propertyBag))
			{
				return null;
			}
					propertyBag.Read(propertyName, out object? value, IntPtr.Zero);
			return value as string;
		}
		catch (Exception)
		{
			return null;
		}
		finally
		{
			if (ppvObj != null)
			{
				Marshal.ReleaseComObject(ppvObj);
			}
		}
	}

	private static string? GetDisplayName(IMoniker moniker)
	{
		try
		{
			moniker.GetDisplayName(null!, null!, out string ppszDisplayName);
			return ppszDisplayName;
		}
		catch (Exception)
		{
			return null;
		}
	}
}
