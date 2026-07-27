using System;
using System.Runtime.InteropServices;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
internal interface IMFSourceReader
{
	[PreserveSig]
	int GetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] out bool selected);

	[PreserveSig]
	int SetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);

	[PreserveSig]
	int GetNativeMediaType(int streamIndex, int mediaTypeIndex, out IMFMediaType mediaType);

	[PreserveSig]
	int GetCurrentMediaType(int streamIndex, out IMFMediaType mediaType);

	[PreserveSig]
	int SetCurrentMediaType(int streamIndex, nint reserved, IMFMediaType mediaType);

	[PreserveSig]
	int SetCurrentPosition(in Guid timeFormat, nint position);

	[PreserveSig]
	int ReadSample(int streamIndex, int controlFlags, out int actualStreamIndex, out int streamFlags, out long timestamp, [MarshalAs(UnmanagedType.IUnknown)] out object? sample);

	[PreserveSig]
	int Flush(int streamIndex);

	[PreserveSig]
	int GetServiceForStream(int streamIndex, in Guid service, in Guid riid, out nint value);

	[PreserveSig]
	int GetPresentationAttribute(int streamIndex, in Guid attribute, nint value);
}
