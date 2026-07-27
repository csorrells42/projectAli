using System;
using System.Runtime.InteropServices;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("eb533d5d-2db6-40f8-97a9-494692014f07")]
internal interface IMFDXGIDeviceManager
{
	[PreserveSig]
	int CloseDeviceHandle(nint deviceHandle);

	[PreserveSig]
	int GetVideoService(nint deviceHandle, in Guid riid, out nint service);

	[PreserveSig]
	int LockDevice(nint deviceHandle, in Guid riid, out nint device, [MarshalAs(UnmanagedType.Bool)] bool block);

	[PreserveSig]
	int OpenDeviceHandle(out nint deviceHandle);

	[PreserveSig]
	int ResetDevice(nint device, int resetToken);

	[PreserveSig]
	int TestDevice(nint deviceHandle);

	[PreserveSig]
	int UnlockDevice(nint deviceHandle, [MarshalAs(UnmanagedType.Bool)] bool saveState);
}
