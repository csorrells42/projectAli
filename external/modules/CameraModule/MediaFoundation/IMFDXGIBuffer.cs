using System;
using System.Runtime.InteropServices;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("e7174cfa-1c9e-48b1-8866-626226bfc258")]
internal interface IMFDXGIBuffer
{
	[PreserveSig]
	int GetResource(in Guid riid, out nint resource);

	[PreserveSig]
	int GetSubresourceIndex(out int subresource);

	[PreserveSig]
	int GetUnknown(in Guid guid, in Guid riid, out nint unknown);

	[PreserveSig]
	int SetUnknown(in Guid guid, [MarshalAs(UnmanagedType.IUnknown)] object? unknown);
}
