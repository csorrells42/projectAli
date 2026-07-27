using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

internal static class MediaFoundationInterop
{
	public const int MF_VERSION = 131184;

	public const int MFSTARTUP_FULL = 0;

	public const int MF_SOURCE_READER_ANY_STREAM = -2;

	public const int MF_SOURCE_READER_MEDIASOURCE = -1;

	public const int MF_SOURCE_READER_FIRST_VIDEO_STREAM = -4;

	public const int MF_SOURCE_READERF_ENDOFSTREAM = 2;

	public const int MF_E_NO_MORE_TYPES = -1072875847;

	public const int MFVideoInterlace_Progressive = 2;

	public const long TicksPerSecond = 10000000L;

	public static void ThrowIfFailed(int result)
	{
		if (result < 0)
		{
			Marshal.ThrowExceptionForHR(result);
		}
	}

	public static bool Failed(int result)
	{
		return result < 0;
	}

	public static long PackRatio(int numerator, int denominator)
	{
		return ((long)numerator << 32) | (uint)Math.Max(1, denominator);
	}

	public static void ReleaseComObject(object? instance)
	{
		if (instance != null && Marshal.IsComObject(instance))
		{
			Marshal.ReleaseComObject(instance);
		}
	}

	public static string? GetAllocatedString(IMFAttributes attributes, Guid key)
	{
		if (Failed(attributes.GetAllocatedString(in key, out var value, out var length)) || value == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			return Marshal.PtrToStringUni(value, length);
		}
		finally
		{
			CoTaskMemFree(value);
		}
	}

	public static string? GetString(IMFAttributes attributes, Guid key)
	{
		if (Failed(attributes.GetStringLength(in key, out var length)))
		{
			return null;
		}
		StringBuilder stringBuilder = new StringBuilder(length + 1);
		if (!Failed(attributes.GetString(in key, stringBuilder, length + 1, out var _)))
		{
			return stringBuilder.ToString();
		}
		return null;
	}

	public static bool TryGetFrameSize(IMFAttributes attributes, out int width, out int height)
	{
		width = 0;
		height = 0;
		if (Failed(attributes.GetUINT64(in MediaFoundationGuids.MF_MT_FRAME_SIZE, out var value)))
		{
			return false;
		}
		width = (int)(value >> 32);
		height = (int)(value & 0xFFFFFFFFu);
		if (width > 0)
		{
			return height > 0;
		}
		return false;
	}

	public static bool TryGetFrameRate(IMFAttributes attributes, out double framesPerSecond)
	{
		framesPerSecond = 0.0;
		if (Failed(attributes.GetUINT64(in MediaFoundationGuids.MF_MT_FRAME_RATE, out var value)))
		{
			return false;
		}
		int num = (int)(value >> 32);
		int num2 = (int)(value & 0xFFFFFFFFu);
		if (num <= 0 || num2 <= 0)
		{
			return false;
		}
		framesPerSecond = (double)num / (double)num2;
		return framesPerSecond > 0.0;
	}

	public static string FormatSubtype(Guid subtype)
	{
		if (!(subtype == MediaFoundationGuids.MFVideoFormat_RGB32))
		{
			if (!(subtype == MediaFoundationGuids.MFVideoFormat_NV12))
			{
				if (!(subtype == MediaFoundationGuids.MFVideoFormat_P010))
				{
					if (!(subtype == MediaFoundationGuids.MFVideoFormat_H264))
					{
						if (!TryFormatFourCc(subtype, out string value))
						{
							return subtype.ToString("N").Substring(0, 8);
						}
						return value;
					}
					return "h264";
				}
				return "p010";
			}
			return "nv12";
		}
		return "rgb32";
	}

	private static bool TryFormatFourCc(Guid subtype, out string value)
	{
		value = string.Empty;
		byte[] array = subtype.ToByteArray();
		ushort num = BitConverter.ToUInt16(array, 4);
		ushort num2 = BitConverter.ToUInt16(array, 6);
		if (num != 0 || num2 != 16)
		{
			return false;
		}
		byte[] subArray = array[..4];
		if (subArray.Any((byte character) => character < 32 || character > 126))
		{
			return false;
		}
		value = Encoding.ASCII.GetString(subArray).Trim().ToLowerInvariant();
		return !string.IsNullOrWhiteSpace(value);
	}

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFStartup(int version, int flags);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFShutdown();

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateMediaType(out IMFMediaType mediaType);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateSample(out IMFSample sample);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateDXGIDeviceManager(out int resetToken, out IMFDXGIDeviceManager deviceManager);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFGetDXGIDeviceManageMode([MarshalAs(UnmanagedType.IUnknown)] object deviceManager, out int mode);

	[DllImport("mf.dll", ExactSpelling = true)]
	public static extern int MFEnumDeviceSources(IMFAttributes attributes, out nint activateArray, out int count);

	[DllImport("mf.dll", ExactSpelling = true)]
	public static extern int MFCreateDeviceSource(IMFAttributes attributes, [MarshalAs(UnmanagedType.IUnknown)] out object? mediaSource);

	[DllImport("mfreadwrite.dll", ExactSpelling = true)]
	public static extern int MFCreateSourceReaderFromMediaSource([MarshalAs(UnmanagedType.IUnknown)] object mediaSource, IMFAttributes? attributes, out IMFSourceReader reader);

	[DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
	public static extern int MFCreateSinkWriterFromURL(string outputUrl, nint byteStream, IMFAttributes? attributes, out IMFSinkWriter sinkWriter);

	[DllImport("ole32.dll", ExactSpelling = true)]
	private static extern void CoTaskMemFree(nint pointer);

	[DllImport("d3d12.dll", ExactSpelling = true)]
	public static extern int D3D12CreateDevice(nint adapter, int minimumFeatureLevel, in Guid riid, out nint device);

	[DllImport("d3d11.dll", ExactSpelling = true)]
	public static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, int flags, int[]? featureLevels, int featureLevelsCount, int sdkVersion, out nint device, out int featureLevel, out nint immediateContext);
}
