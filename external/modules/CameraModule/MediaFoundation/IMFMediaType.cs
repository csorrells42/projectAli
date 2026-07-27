using System.Runtime.InteropServices;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
internal interface IMFMediaType : IMFAttributes
{
}
