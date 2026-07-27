using System;
using System.Runtime.InteropServices;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67")]
internal interface IMFActivate : IMFAttributes
{
	[PreserveSig]
	int ActivateObject(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? objectInstance);

	[PreserveSig]
	int ShutdownObject();

	[PreserveSig]
	int DetachObject();
}
