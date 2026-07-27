using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AvatarBuilder.Modules.Webcam.Common;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

internal static class MediaFoundationCameraDeviceFactory
{
	public sealed class MediaFoundationScope : IDisposable
	{
		private bool _disposed;

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
			lock (StartupLock)
			{
				if (_startupReferences > 0)
				{
					_startupReferences--;
					if (_startupReferences == 0)
					{
						MediaFoundationInterop.MFShutdown();
					}
				}
			}
		}
	}

	private static readonly object StartupLock = new object();

	private static int _startupReferences;

	public static MediaFoundationScope Startup()
	{
		lock (StartupLock)
		{
			if (_startupReferences == 0)
			{
				MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFStartup(131184, 0));
			}
			_startupReferences++;
		}
		return new MediaFoundationScope();
	}

	public static IMFSourceReader CreateModeProbeReader(CameraDevice camera, out object mediaSource)
	{
		mediaSource = null!;
		IMFActivate iMFActivate = FindCameraActivate(camera) ?? throw new InvalidOperationException("Media Foundation could not find camera: " + camera.Name);
		try
		{
			mediaSource = CreateMediaSource(iMFActivate, camera.Name);
			MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateAttributes(out IMFAttributes attributes, 1));
			try
			{
				MediaFoundationInterop.ThrowIfFailed(attributes.SetUINT32(in MediaFoundationGuids.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1));
				MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateSourceReaderFromMediaSource(mediaSource, attributes, out IMFSourceReader reader));
				return reader;
			}
			finally
			{
				MediaFoundationInterop.ReleaseComObject(attributes);
			}
		}
		finally
		{
			MediaFoundationInterop.ReleaseComObject(iMFActivate);
		}
	}

	public static IMFSourceReader CreatePreviewReader(CameraDevice camera, CameraVideoMode? mode, out object mediaSource)
	{
		mediaSource = null!;
		IMFActivate iMFActivate = FindCameraActivate(camera) ?? throw new InvalidOperationException("Media Foundation could not find camera: " + camera.Name);
		try
		{
			mediaSource = CreateMediaSource(iMFActivate, camera.Name);
			IMFAttributes attributes;
			int num = MediaFoundationInterop.MFCreateAttributes(out attributes, 4);
			if (MediaFoundationInterop.Failed(num))
			{
				throw new InvalidOperationException($"Media Foundation source-reader attributes failed: 0x{num:X8}");
			}
			try
			{
				MediaFoundationInterop.ThrowIfFailed(attributes.SetUINT32(in MediaFoundationGuids.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1));
				MediaFoundationInterop.ThrowIfFailed(attributes.SetUINT32(in MediaFoundationGuids.MF_LOW_LATENCY, 1));
				MediaFoundationInterop.ThrowIfFailed(attributes.SetUINT32(in MediaFoundationGuids.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1));
				IMFSourceReader reader;
				int num2 = MediaFoundationInterop.MFCreateSourceReaderFromMediaSource(mediaSource, attributes, out reader);
				if (MediaFoundationInterop.Failed(num2))
				{
					throw new InvalidOperationException($"Media Foundation source-reader creation failed: 0x{num2:X8}");
				}
				try
				{
					ConfigurePreviewReader(reader, mode);
					return reader;
				}
				catch
				{
					MediaFoundationInterop.ReleaseComObject(reader);
					throw;
				}
			}
			finally
			{
				MediaFoundationInterop.ReleaseComObject(attributes);
			}
		}
		finally
		{
			MediaFoundationInterop.ReleaseComObject(iMFActivate);
		}
	}

	public static IMFSourceReader CreateTextureSourceReader(string cameraName, CameraVideoMode? mode, IMFDXGIDeviceManager d3dManager, out object mediaSource, bool enableAdvancedVideoProcessing = true, Guid? preferredSubtype = null, bool configureMediaType = true)
	{
		return CreateTextureSourceReader(new CameraDevice(-1, cameraName, string.Empty), mode, d3dManager, out mediaSource, enableAdvancedVideoProcessing, preferredSubtype, configureMediaType);
	}

	public static IMFSourceReader CreateTextureSourceReader(CameraDevice camera, CameraVideoMode? mode, IMFDXGIDeviceManager d3dManager, out object mediaSource, bool enableAdvancedVideoProcessing = true, Guid? preferredSubtype = null, bool configureMediaType = true)
	{
		mediaSource = null!;
		IMFActivate iMFActivate = FindCameraActivate(camera) ?? throw new InvalidOperationException("Media Foundation could not find camera: " + camera.Name);
		try
		{
			mediaSource = CreateMediaSource(iMFActivate, camera.Name);
			MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateAttributes(out IMFAttributes attributes, 6));
			try
			{
				MediaFoundationInterop.ThrowIfFailed(attributes.SetUINT32(in MediaFoundationGuids.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1));
				MediaFoundationInterop.ThrowIfFailed(attributes.SetUINT32(in MediaFoundationGuids.MF_LOW_LATENCY, 1));
				if (enableAdvancedVideoProcessing)
				{
					MediaFoundationInterop.ThrowIfFailed(attributes.SetUINT32(in MediaFoundationGuids.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1));
				}
				MediaFoundationInterop.ThrowIfFailed(attributes.SetUnknown(in MediaFoundationGuids.MF_SOURCE_READER_D3D_MANAGER, d3dManager));
				MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateSourceReaderFromMediaSource(mediaSource, attributes, out IMFSourceReader reader));
				try
				{
					if (configureMediaType)
					{
						ConfigureTextureReader(reader, mode, preferredSubtype);
					}
					return reader;
				}
				catch
				{
					MediaFoundationInterop.ReleaseComObject(reader);
					throw;
				}
			}
			finally
			{
				MediaFoundationInterop.ReleaseComObject(attributes);
			}
		}
		finally
		{
			MediaFoundationInterop.ReleaseComObject(iMFActivate);
		}
	}

	public static object CreateMediaSource(IMFActivate activate, string cameraName)
	{
		object? objectInstance;
		int num = activate.ActivateObject(new Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66"), out objectInstance);
		if (!MediaFoundationInterop.Failed(num) && objectInstance != null)
		{
			return objectInstance;
		}
		string? allocatedString = MediaFoundationInterop.GetAllocatedString(activate, MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
		if (string.IsNullOrWhiteSpace(allocatedString))
		{
			throw new InvalidOperationException($"Media Foundation camera activation failed for {cameraName}: 0x{num:X8}");
		}
		MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateAttributes(out IMFAttributes attributes, 2));
		try
		{
			MediaFoundationInterop.ThrowIfFailed(attributes.SetGUID(in MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, in MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID));
			MediaFoundationInterop.ThrowIfFailed(attributes.SetString(in MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, allocatedString));
			object? mediaSource;
			int num2 = MediaFoundationInterop.MFCreateDeviceSource(attributes, out mediaSource);
			if (MediaFoundationInterop.Failed(num2) || mediaSource == null)
			{
				throw new InvalidOperationException($"Media Foundation device-source creation failed for {cameraName}: 0x{num2:X8}");
			}
			return mediaSource;
		}
		finally
		{
			MediaFoundationInterop.ReleaseComObject(attributes);
		}
	}

	public static IReadOnlyList<IMFActivate> EnumerateVideoActivates()
	{
		MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateAttributes(out IMFAttributes attributes, 1));
		try
		{
			MediaFoundationInterop.ThrowIfFailed(attributes.SetGUID(in MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, in MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID));
			MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFEnumDeviceSources(attributes, out var activateArray, out var count));
			try
			{
				List<IMFActivate> list = new List<IMFActivate>();
				for (int i = 0; i < count; i++)
				{
					nint num = Marshal.ReadIntPtr(activateArray, i * IntPtr.Size);
					if (num != IntPtr.Zero)
					{
						list.Add((IMFActivate)Marshal.GetObjectForIUnknown(num));
						Marshal.Release(num);
					}
				}
				return list;
			}
			finally
			{
				Marshal.FreeCoTaskMem(activateArray);
			}
		}
		finally
		{
			MediaFoundationInterop.ReleaseComObject(attributes);
		}
	}

	private static IMFActivate? FindCameraActivate(CameraDevice camera)
	{
		IReadOnlyList<IMFActivate> readOnlyList = EnumerateVideoActivates();
			IMFActivate? iMFActivate = null;
		string? text = CameraDeviceCatalog.TryCreatePhysicalDeviceKey(camera);
		foreach (IMFActivate item in readOnlyList)
		{
			string? allocatedString = MediaFoundationInterop.GetAllocatedString(item, MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
			string? allocatedString2 = MediaFoundationInterop.GetAllocatedString(item, MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
			if (!string.IsNullOrWhiteSpace(camera.DevicePath) && string.Equals(allocatedString2, camera.DevicePath, StringComparison.OrdinalIgnoreCase))
			{
				ReleaseAllExcept(readOnlyList, item);
				return item;
			}
			if (string.Equals(allocatedString, camera.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(allocatedString2, camera.Name, StringComparison.OrdinalIgnoreCase))
			{
				ReleaseAllExcept(readOnlyList, item);
				return item;
			}
			string? a = (string.IsNullOrWhiteSpace(allocatedString2) ? null : CameraDeviceCatalog.TryCreatePhysicalDeviceKey(new CameraDevice(-1, allocatedString ?? "", allocatedString2, "Media Foundation")));
			if (!string.IsNullOrWhiteSpace(text) && string.Equals(a, text, StringComparison.OrdinalIgnoreCase))
			{
				ReleaseAllExcept(readOnlyList, item);
				return item;
			}
			if (iMFActivate == null && ((allocatedString != null && allocatedString.Contains(camera.Name, StringComparison.OrdinalIgnoreCase)) || (allocatedString2 != null && allocatedString2.Contains(camera.Name, StringComparison.OrdinalIgnoreCase))))
			{
				iMFActivate = item;
			}
		}
		if (iMFActivate != null)
		{
			ReleaseAllExcept(readOnlyList, iMFActivate);
		}
		else
		{
			foreach (IMFActivate item2 in readOnlyList)
			{
				MediaFoundationInterop.ReleaseComObject(item2);
			}
		}
		return iMFActivate;
	}

	private static void ReleaseAllExcept(IReadOnlyList<IMFActivate> candidates, IMFActivate keep)
	{
		foreach (IMFActivate candidate in candidates)
		{
			if (candidate != keep)
			{
				MediaFoundationInterop.ReleaseComObject(candidate);
			}
		}
	}

	private static void ConfigurePreviewReader(IMFSourceReader reader, CameraVideoMode? mode)
	{
		if (mode != null && !mode.IsAuto)
		{
			int? width = mode.Width;
			if (width.HasValue && width.GetValueOrDefault() > 0)
			{
				int? height = mode.Height;
				if (height.HasValue && height.GetValueOrDefault() > 0)
				{
					if (TrySetSelectedPreviewMediaType(reader, mode))
					{
						return;
					}
					throw new InvalidOperationException("Media Foundation could not keep selected mode " + mode.Label + "; trying fallback camera path.");
				}
			}
		}
		if (TrySetPreviewMediaType(reader, mode, MediaFoundationGuids.MFVideoFormat_NV12, requestFrameSize: true, requestFrameRate: true) || TrySetPreviewMediaType(reader, mode, MediaFoundationGuids.MFVideoFormat_NV12, requestFrameSize: false, requestFrameRate: false) || TrySetPreviewMediaType(reader, mode, MediaFoundationGuids.MFVideoFormat_RGB32, requestFrameSize: true, requestFrameRate: true) || TrySetPreviewMediaType(reader, mode, MediaFoundationGuids.MFVideoFormat_RGB32, requestFrameSize: false, requestFrameRate: false))
		{
			return;
		}
		throw new InvalidOperationException("Media Foundation could not configure NV12 or RGB32 preview frames.");
	}

	private static bool TrySetSelectedPreviewMediaType(IMFSourceReader reader, CameraVideoMode mode)
	{
		Guid[] array = new Guid[2]
		{
			MediaFoundationGuids.MFVideoFormat_NV12,
			MediaFoundationGuids.MFVideoFormat_RGB32
		};
		Guid[] array2 = array;
		foreach (Guid subtype in array2)
		{
			if (TrySetPreviewMediaType(reader, mode, subtype, requestFrameSize: true, requestFrameRate: true) && CurrentMatchesRequestedResolution(reader, mode))
			{
				return true;
			}
		}
		array2 = array;
		foreach (Guid subtype2 in array2)
		{
			if (TrySetPreviewMediaType(reader, mode, subtype2, requestFrameSize: true, requestFrameRate: false) && CurrentMatchesRequestedResolution(reader, mode))
			{
				return true;
			}
		}
		return false;
	}

	private static bool TrySetPreviewMediaType(IMFSourceReader reader, CameraVideoMode? mode, Guid subtype, bool requestFrameSize, bool requestFrameRate)
	{
		int numerator = mode?.Width ?? 1280;
		int denominator = mode?.Height ?? 720;
		var (numerator2, denominator2) = CreateFrameRateRatio(mode?.FramesPerSecond ?? 30.0);
		MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateMediaType(out IMFMediaType mediaType));
		try
		{
			MediaFoundationInterop.ThrowIfFailed(mediaType.SetGUID(in MediaFoundationGuids.MF_MT_MAJOR_TYPE, in MediaFoundationGuids.MFMediaType_Video));
			MediaFoundationInterop.ThrowIfFailed(mediaType.SetGUID(in MediaFoundationGuids.MF_MT_SUBTYPE, in subtype));
			MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT32(in MediaFoundationGuids.MF_MT_INTERLACE_MODE, 2));
			if (requestFrameSize)
			{
				MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT64(in MediaFoundationGuids.MF_MT_FRAME_SIZE, MediaFoundationInterop.PackRatio(numerator, denominator)));
			}
			if (requestFrameRate)
			{
				MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT64(in MediaFoundationGuids.MF_MT_FRAME_RATE, MediaFoundationInterop.PackRatio(numerator2, denominator2)));
			}
			return !MediaFoundationInterop.Failed(reader.SetCurrentMediaType(-4, IntPtr.Zero, mediaType));
		}
		finally
		{
			MediaFoundationInterop.ReleaseComObject(mediaType);
		}
	}

	private static bool CurrentMatchesRequestedResolution(IMFSourceReader reader, CameraVideoMode mode)
	{
		if (MediaFoundationInterop.Failed(reader.GetCurrentMediaType(-4, out IMFMediaType mediaType)))
		{
			return false;
		}
		try
		{
			int width;
			int height;
			return MediaFoundationInterop.TryGetFrameSize(mediaType, out width, out height) && width == mode.Width && height == mode.Height;
		}
		finally
		{
			MediaFoundationInterop.ReleaseComObject(mediaType);
		}
	}

	private static void ConfigureTextureReader(IMFSourceReader reader, CameraVideoMode? mode, Guid? preferredSubtype)
	{
		object obj;
		if (!preferredSubtype.HasValue)
		{
			obj = new Guid[2]
			{
				MediaFoundationGuids.MFVideoFormat_NV12,
				MediaFoundationGuids.MFVideoFormat_P010
			};
		}
		else
		{
			Guid valueOrDefault = preferredSubtype.GetValueOrDefault();
			obj = new Guid[1] { valueOrDefault };
		}
		Guid[] array = (Guid[])obj;
		foreach (Guid subtype in array)
		{
			if (TrySetTextureMediaType(reader, mode, subtype, exactMode: true) || TrySetTextureMediaType(reader, mode, subtype, exactMode: false))
			{
				break;
			}
		}
	}

	private static bool TrySetTextureMediaType(IMFSourceReader reader, CameraVideoMode? mode, Guid subtype, bool exactMode)
	{
		int numerator = mode?.Width ?? 1280;
		int denominator = mode?.Height ?? 720;
		var (numerator2, denominator2) = CreateFrameRateRatio(mode?.FramesPerSecond ?? 30.0);
		MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateMediaType(out IMFMediaType mediaType));
		try
		{
			MediaFoundationInterop.ThrowIfFailed(mediaType.SetGUID(in MediaFoundationGuids.MF_MT_MAJOR_TYPE, in MediaFoundationGuids.MFMediaType_Video));
			MediaFoundationInterop.ThrowIfFailed(mediaType.SetGUID(in MediaFoundationGuids.MF_MT_SUBTYPE, in subtype));
			MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT32(in MediaFoundationGuids.MF_MT_INTERLACE_MODE, 2));
			if (exactMode)
			{
				MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT64(in MediaFoundationGuids.MF_MT_FRAME_SIZE, MediaFoundationInterop.PackRatio(numerator, denominator)));
				MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT64(in MediaFoundationGuids.MF_MT_FRAME_RATE, MediaFoundationInterop.PackRatio(numerator2, denominator2)));
			}
			return !MediaFoundationInterop.Failed(reader.SetCurrentMediaType(-4, IntPtr.Zero, mediaType));
		}
		finally
		{
			MediaFoundationInterop.ReleaseComObject(mediaType);
		}
	}

	private static (int Numerator, int Denominator) CreateFrameRateRatio(double fps)
	{
		if (Math.Abs(fps - 29.97) < 0.02)
		{
			return (Numerator: 30000, Denominator: 1001);
		}
		if (Math.Abs(fps - 59.94) < 0.02)
		{
			return (Numerator: 60000, Denominator: 1001);
		}
		return (Numerator: (int)Math.Round(Math.Clamp(fps, 1.0, 240.0)), Denominator: 1);
	}
}
