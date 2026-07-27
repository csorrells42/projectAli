using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using AvatarBuilder.Modules.Webcam.Common;

namespace AvatarBuilder.Modules.Webcam.DirectShow;

public sealed class DirectShowCameraControlService
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

	[ComImport]
	[Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IBaseFilter
	{
	}

	[ComImport]
	[Guid("C6E13370-30AC-11d0-A18C-00A0C9118956")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAMCameraControl
	{
		[PreserveSig]
		int GetRange(int property, out int min, out int max, out int steppingDelta, out int defaultValue, out int capsFlags);

		[PreserveSig]
		int Set(int property, int value, int flags);

		[PreserveSig]
		int Get(int property, out int value, out int flags);
	}

	[ComImport]
	[Guid("C6E13360-30AC-11d0-A18C-00A0C9118956")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAMVideoProcAmp
	{
		[PreserveSig]
		int GetRange(int property, out int min, out int max, out int steppingDelta, out int defaultValue, out int capsFlags);

		[PreserveSig]
		int Set(int property, int value, int flags);

		[PreserveSig]
		int Get(int property, out int value, out int flags);
	}

	private const int CameraControlFlagAuto = 1;

	private const int CameraControlFlagManual = 2;

	private static readonly Guid SystemDeviceEnumClsid = new Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");

	private static readonly Guid VideoInputDeviceCategory = new Guid("860BB310-5D01-11d0-BD3B-00A0C911CE86");

	private static readonly IReadOnlyList<(int Id, string Name)> CameraProperties =
	[
		(0, "Pan"),
		(1, "Tilt"),
		(2, "Roll"),
		(3, "Zoom"),
		(4, "Exposure"),
		(5, "Iris"),
		(6, "Focus")
	];

	private static readonly IReadOnlyList<(int Id, string Name)> VideoProcAmpProperties =
	[
		(0, "Brightness"),
		(1, "Contrast"),
		(2, "Hue"),
		(3, "Saturation"),
		(4, "Sharpness"),
		(5, "Gamma"),
		(6, "Color Enable"),
		(7, "White Balance"),
		(8, "Backlight"),
		(9, "Gain")
	];

	public IReadOnlyList<CameraControlItem> GetControls(CameraDevice camera)
	{
		return WithCameraFilter(camera, delegate(object filter)
		{
			List<CameraControlItem> list = new List<CameraControlItem>();
			if (filter is IAMCameraControl cameraControl)
			{
				foreach (var cameraProperty in CameraProperties)
				{
					if (TryReadCameraControl(cameraControl, cameraProperty.Id, cameraProperty.Name, out CameraControlItem? item))
					{
						list.Add(item);
					}
				}
			}
			if (filter is IAMVideoProcAmp videoProcAmp)
			{
				foreach (var videoProcAmpProperty in VideoProcAmpProperties)
				{
					if (TryReadVideoProcAmpControl(videoProcAmp, videoProcAmpProperty.Id, videoProcAmpProperty.Name, out CameraControlItem? item2))
					{
						list.Add(item2);
					}
				}
			}
			return list;
		}) ?? new List<CameraControlItem>();
	}

	public bool SetControl(CameraDevice camera, CameraControlItem control, int value, bool isAuto)
	{
		return WithCameraFilter(camera, delegate(object filter)
		{
			int flags = (isAuto ? 1 : 2);
			if (control.Kind == CameraControlKind.Camera && filter is IAMCameraControl iAMCameraControl)
			{
				return iAMCameraControl.Set(control.PropertyId, value, flags) == 0;
			}
			return control.Kind == CameraControlKind.VideoProcAmp && filter is IAMVideoProcAmp iAMVideoProcAmp && iAMVideoProcAmp.Set(control.PropertyId, value, flags) == 0;
		});
	}

	private static bool TryReadCameraControl(IAMCameraControl cameraControl, int propertyId, string name, [NotNullWhen(true)] out CameraControlItem? item)
	{
		item = null;
		if (cameraControl.GetRange(propertyId, out var min, out var max, out var steppingDelta, out var defaultValue, out var capsFlags) != 0)
		{
			return false;
		}
		if (cameraControl.Get(propertyId, out var value, out var flags) != 0)
		{
			value = defaultValue;
			flags = capsFlags;
		}
		item = new CameraControlItem(CameraControlKind.Camera, propertyId, name, min, max, steppingDelta, defaultValue, value, (flags & 1) != 0, (capsFlags & 1) != 0);
		return true;
	}

	private static bool TryReadVideoProcAmpControl(IAMVideoProcAmp videoProcAmp, int propertyId, string name, [NotNullWhen(true)] out CameraControlItem? item)
	{
		item = null;
		if (videoProcAmp.GetRange(propertyId, out var min, out var max, out var steppingDelta, out var defaultValue, out var capsFlags) != 0)
		{
			return false;
		}
		if (videoProcAmp.Get(propertyId, out var value, out var flags) != 0)
		{
			value = defaultValue;
			flags = capsFlags;
		}
		item = new CameraControlItem(CameraControlKind.VideoProcAmp, propertyId, name, min, max, steppingDelta, defaultValue, value, (flags & 1) != 0, (capsFlags & 1) != 0);
		return true;
	}

	private static T? WithCameraFilter<T>(CameraDevice camera, Func<object, T> action)
	{
		object? obj = null;
		IEnumMoniker? enumMoniker = null;
		object? ppvResult = null;
		try
		{
			Type deviceEnumeratorType = Type.GetTypeFromCLSID(SystemDeviceEnumClsid, throwOnError: true)
				?? throw new InvalidOperationException("DirectShow system device enumerator type is unavailable.");
			obj = Activator.CreateInstance(deviceEnumeratorType);
			if (!(obj is ICreateDevEnum createDevEnum))
			{
				return default(T);
			}
			Guid deviceClass = VideoInputDeviceCategory;
			if (createDevEnum.CreateClassEnumerator(ref deviceClass, out enumMoniker, 0) != 0 || enumMoniker == null)
			{
				return default(T);
			}
			IMoniker[] array = new IMoniker[1];
			while (enumMoniker.Next(1, array, IntPtr.Zero) == 0)
			{
				IMoniker moniker = array[0];
				try
				{
					string? name = ReadProperty(moniker, "FriendlyName");
					string? path = ReadProperty(moniker, "DevicePath") ?? GetDisplayName(moniker);
					if (!CameraMatches(camera, name, path))
					{
						continue;
					}
					Guid riidResult = typeof(IBaseFilter).GUID;
					moniker.BindToObject(null!, null!, ref riidResult, out ppvResult);
					return (ppvResult == null) ? default(T) : action(ppvResult);
				}
				finally
				{
					Marshal.ReleaseComObject(moniker);
				}
			}
			return default(T);
		}
		catch
		{
			return default(T);
		}
		finally
		{
			if (ppvResult != null)
			{
				Marshal.ReleaseComObject(ppvResult);
			}
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

	private static bool CameraMatches(CameraDevice camera, string? name, string? path)
	{
		return camera.EnumerateSourceDevices().Any((CameraDevice sourceDevice) => string.Equals(path, sourceDevice.DevicePath, StringComparison.OrdinalIgnoreCase) || string.Equals(name, sourceDevice.Name, StringComparison.OrdinalIgnoreCase));
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
		catch
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
		catch
		{
			return null;
		}
	}
}
