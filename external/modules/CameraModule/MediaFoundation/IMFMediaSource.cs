using System;
using System.Runtime.InteropServices;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66")]
internal interface IMFMediaSource
{
	[PreserveSig]
	int GetEvent(int flags, out nint mediaEvent);

	[PreserveSig]
	int BeginGetEvent(nint callback, nint state);

	[PreserveSig]
	int EndGetEvent(nint result, out nint mediaEvent);

	[PreserveSig]
	int QueueEvent(int eventType, in Guid extendedType, int status, nint value);

	[PreserveSig]
	int GetCharacteristics(out int characteristics);

	[PreserveSig]
	int CreatePresentationDescriptor(out nint presentationDescriptor);

	[PreserveSig]
	int Start(nint presentationDescriptor, in Guid timeFormat, nint startPosition);

	[PreserveSig]
	int Stop();

	[PreserveSig]
	int Pause();

	[PreserveSig]
	int Shutdown();
}
