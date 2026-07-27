using System;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

internal static class MediaFoundationGuids
{
	public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE = new Guid(3322594814u, 9514, 18319, 160, 239, 188, 143, 165, 247, 202, 211);

	public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID = new Guid(2328057978u, 19175, 17112, 153, 224, 10, 96, 19, 238, 249, 15);

	public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME = new Guid(1624302937, 21240, 20386, 187, 206, 172, 219, 52, 168, 236, 1);

	public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK = new Guid(1492167384, 8895, 20362, 187, 61, 210, 196, 151, 140, 110, 47);

	public static readonly Guid MFMediaType_Video = new Guid(1935960438, 0, 16, 128, 0, 0, 170, 0, 56, 155, 113);

	public static readonly Guid MFVideoFormat_RGB32 = new Guid(22, 0, 16, 128, 0, 0, 170, 0, 56, 155, 113);

	public static readonly Guid MFVideoFormat_NV12 = new Guid(842094158, 0, 16, 128, 0, 0, 170, 0, 56, 155, 113);

	public static readonly Guid MFVideoFormat_P010 = new Guid(808530000, 0, 16, 128, 0, 0, 170, 0, 56, 155, 113);

	public static readonly Guid MFVideoFormat_H264 = new Guid(875967048, 0, 16, 128, 0, 0, 170, 0, 56, 155, 113);

	public static readonly Guid MF_MT_MAJOR_TYPE = new Guid(1223401870u, 63689, 18055, 191, 17, 10, 116, 201, 249, 106, 143);

	public static readonly Guid MF_MT_SUBTYPE = new Guid(4158868634u, 17128, 18196, 183, 75, 203, 41, 215, 44, 53, 229);

	public static readonly Guid MF_MT_FRAME_SIZE = new Guid(374522685u, 54962, 16402, 184, 52, 114, 3, 8, 73, 163, 125);

	public static readonly Guid MF_MT_FRAME_RATE = new Guid(3294208744u, 15660, 20036, 177, 50, 254, 229, 21, 108, 123, 176);

	public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new Guid(3325520414u, 36106, 16423, 190, 69, 109, 154, 10, 211, 155, 182);

	public static readonly Guid MF_MT_INTERLACE_MODE = new Guid(3799141304u, 58998, 18438, 180, 178, 168, 214, 239, 180, 76, 205);

	public static readonly Guid MF_MT_AVG_BITRATE = new Guid(540223012u, 64269, 19870, 189, 13, 203, 246, 120, 108, 16, 46);

	public static readonly Guid MF_MT_DEFAULT_STRIDE = new Guid(1682656840, 7682, 17686, 176, 235, 192, 28, 169, 212, 154, 198);

	public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new Guid(3373741881u, 24150, 17948, 183, 19, 70, 251, 153, 92, 185, 95);

	public static readonly Guid MF_MT_FIXED_SIZE_SAMPLES = new Guid(3102470063u, 46872, 19972, 176, 169, 17, 103, 117, 227, 50, 27);

	public static readonly Guid MFSampleExtension_CleanPoint = new Guid(2631860696u, 41200, 17338, 176, 119, 234, 160, 108, 189, 114, 138);

	public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new Guid(2788469020u, 33323, 16825, 164, 148, 77, 228, 100, 54, 18, 176);

	public static readonly Guid MF_LOW_LATENCY = new Guid(2619836698u, 60794, 16609, 136, 232, 178, 39, 39, 160, 36, 238);

	public static readonly Guid MF_SOURCE_READER_D3D_MANAGER = new Guid(3967954338u, 57833, 19241, 160, 216, 86, 60, 113, 159, 82, 105);

	public static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new Guid(4214837053u, 52465, 17134, 187, 179, 249, 184, 69, 213, 104, 29);

	public static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new Guid(260168236u, 46391, 18034, 168, 178, 166, 129, 177, 115, 7, 163);

	public static readonly Guid MF_PD_DURATION = new Guid(1821969715u, 48014, 18298, 133, 152, 13, 93, 150, 252, 216, 138);
}
