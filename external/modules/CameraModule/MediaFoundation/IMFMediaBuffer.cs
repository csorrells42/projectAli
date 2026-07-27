using System.Runtime.InteropServices;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("045FA593-8799-42b8-BC8D-8968C6453507")]
internal interface IMFMediaBuffer
{
	[PreserveSig]
	int Lock(out nint buffer, out int maxLength, out int currentLength);

	[PreserveSig]
	int Unlock();

	[PreserveSig]
	int GetCurrentLength(out int currentLength);

	[PreserveSig]
	int SetCurrentLength(int currentLength);

	[PreserveSig]
	int GetMaxLength(out int maxLength);
}
