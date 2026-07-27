using System;
using System.Runtime.InteropServices;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d")]
internal interface IMFSinkWriter
{
	[PreserveSig]
	int AddStream(IMFMediaType targetMediaType, out int streamIndex);

	[PreserveSig]
	int SetInputMediaType(int streamIndex, IMFMediaType inputMediaType, IMFAttributes? encodingParameters);

	[PreserveSig]
	int BeginWriting();

	[PreserveSig]
	int WriteSample(int streamIndex, IMFSample sample);

	[PreserveSig]
	int SendStreamTick(int streamIndex, long timestamp);

	[PreserveSig]
	int PlaceMarker(int streamIndex, nint context);

	[PreserveSig]
	int NotifyEndOfSegment(int streamIndex);

	[PreserveSig]
	int Flush(int streamIndex);

	[PreserveSig]
	int Finalize_();

	[PreserveSig]
	int GetServiceForStream(int streamIndex, in Guid service, in Guid riid, out nint value);

	[PreserveSig]
	int GetStatistics(int streamIndex, nint statistics);
}
