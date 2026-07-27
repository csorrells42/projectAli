using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectX11;
using AvatarBuilder.Modules.Webcam.MediaFoundation;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public static class TextureNativeCameraRecorder
{
	public static TextureNativeCameraRecordingSession StartSession(string cameraName, CameraVideoMode? mode, string path, TextureNativeRecordingOptions? options = null)
	{
		return new TextureNativeCameraRecordingSession(cameraName, mode, path, options);
	}

	public static Task<TextureNativeRecordingResult> RecordAsync(string cameraName, CameraVideoMode? mode, string path, TimeSpan duration, CancellationToken cancellationToken)
	{
		return Task.Run(() => Record(cameraName, mode, path, duration, cancellationToken), cancellationToken);
	}

	private static TextureNativeRecordingResult Record(string cameraName, CameraVideoMode? mode, string path, TimeSpan duration, CancellationToken cancellationToken)
	{
		using (MediaFoundationCameraDeviceFactory.Startup())
		{
			ITextureNativeDeviceManager? textureNativeDeviceManager = null;
			object? instance = null;
			IMFSourceReader? iMFSourceReader = null;
			MediaFoundationTextureVideoRecorder? mediaFoundationTextureVideoRecorder = null;
			try
			{
				(ITextureNativeDeviceManager DeviceManager, IMFSourceReader Reader, object MediaSource) tuple = OpenTextureSourceReader(cameraName, mode);
				textureNativeDeviceManager = tuple.DeviceManager;
				iMFSourceReader = tuple.Reader;
				instance = tuple.MediaSource;
				(int Width, int Height, double Fps, Guid Subtype) tuple2 = ReadCurrentFormat(iMFSourceReader, mode);
				int item = tuple2.Width;
				int item2 = tuple2.Height;
				double item3 = tuple2.Fps;
				Guid item4 = tuple2.Subtype;
				mediaFoundationTextureVideoRecorder = new MediaFoundationTextureVideoRecorder(path, item, item2, item3, item4, textureNativeDeviceManager.Manager);
				DateTimeOffset utcNow = DateTimeOffset.UtcNow;
				int num = 0;
				long num2 = 0L;
				long num3 = Math.Max(1L, (long)Math.Round(10000000.0 / Math.Clamp(item3, 1.0, 120.0)));
				while (DateTimeOffset.UtcNow - utcNow < duration)
				{
					cancellationToken.ThrowIfCancellationRequested();
					int actualStreamIndex;
					int streamFlags;
					long timestamp;
					object? sample;
					int num4 = iMFSourceReader.ReadSample(-4, 0, out actualStreamIndex, out streamFlags, out timestamp, out sample);
					if (MediaFoundationInterop.Failed(num4))
					{
						return CreateResult(success: false, path, num, textureNativeDeviceManager.ModeName, item4, item, item2, item3, $"Texture-native ReadSample failed: 0x{num4:X8}");
					}
					if ((streamFlags & 2) != 0)
					{
						break;
					}
					if (!(sample is IMFSample iMFSample))
					{
						MediaFoundationInterop.ReleaseComObject(sample);
						continue;
					}
					try
					{
						MediaFoundationInterop.ThrowIfFailed(iMFSample.SetSampleTime(num2));
						MediaFoundationInterop.ThrowIfFailed(iMFSample.SetSampleDuration(num3));
						mediaFoundationTextureVideoRecorder.WriteSample(iMFSample);
						num++;
						num2 += num3;
					}
					finally
					{
						MediaFoundationInterop.ReleaseComObject(sample);
					}
				}
				mediaFoundationTextureVideoRecorder.Stop();
				return CreateResult(num > 0 && File.Exists(path) && new FileInfo(path).Length > 4096, path, num, textureNativeDeviceManager.ModeName, item4, item, item2, item3, (num > 0) ? "Texture-native GPU sample recording completed." : "No texture-native samples were written.");
			}
			catch (Exception ex)
			{
				return CreateResult(success: false, path, mediaFoundationTextureVideoRecorder?.SamplesWritten ?? 0, textureNativeDeviceManager?.ModeName ?? "none", Guid.Empty, (mode?.Width).GetValueOrDefault(), (mode?.Height).GetValueOrDefault(), (mode?.FramesPerSecond).GetValueOrDefault(), ex.Message);
			}
			finally
			{
				mediaFoundationTextureVideoRecorder?.Dispose();
				MediaFoundationInterop.ReleaseComObject(iMFSourceReader);
				MediaFoundationInterop.ReleaseComObject(instance);
				textureNativeDeviceManager?.Dispose();
			}
		}
	}

	internal static (ITextureNativeDeviceManager DeviceManager, IMFSourceReader Reader, object MediaSource) OpenTextureSourceReader(string cameraName, CameraVideoMode? mode)
	{
		return OpenTextureSourceReader(new CameraDevice(-1, cameraName, string.Empty), mode);
	}

	internal static (ITextureNativeDeviceManager DeviceManager, IMFSourceReader Reader, object MediaSource) OpenTextureSourceReader(CameraDevice camera, CameraVideoMode? mode)
	{
		Exception? ex = null;
		Exception? ex2 = null;
		ITextureNativeDeviceManager? textureNativeDeviceManager = null;
		object? instance = null;
		try
		{
			textureNativeDeviceManager = Direct3D12DeviceManager.Create();
			ValidateTextureReader(camera, mode, textureNativeDeviceManager, "D3D12");
			var (item, item2) = CreateTextureSourceReader(camera, mode, textureNativeDeviceManager);
			return (DeviceManager: textureNativeDeviceManager, Reader: item, MediaSource: item2);
		}
		catch (Exception ex3)
		{
			ex = ex3;
			MediaFoundationInterop.ReleaseComObject(instance);
			textureNativeDeviceManager?.Dispose();
		}
		instance = null;
		textureNativeDeviceManager = null;
		try
		{
			textureNativeDeviceManager = Direct3D11DeviceManager.Create();
			var (item3, item4) = CreateTextureSourceReader(camera, mode, textureNativeDeviceManager);
			return (DeviceManager: textureNativeDeviceManager, Reader: item3, MediaSource: item4);
		}
		catch (Exception ex4)
		{
			ex2 = ex4;
			MediaFoundationInterop.ReleaseComObject(instance);
			textureNativeDeviceManager?.Dispose();
		}
		instance = null;
		textureNativeDeviceManager = null;
		IMFSourceReader? iMFSourceReader = null;
		try
		{
			textureNativeDeviceManager = Direct3D12DeviceManager.Create();
			iMFSourceReader = MediaFoundationCameraDeviceFactory.CreatePreviewReader(camera, mode, out instance);
			Guid item5 = ReadCurrentFormat(iMFSourceReader, mode).Subtype;
			if (item5 != MediaFoundationGuids.MFVideoFormat_NV12)
			{
				throw new InvalidOperationException("System-memory camera fallback negotiated " + MediaFoundationInterop.FormatSubtype(item5) + " instead of NV12.");
			}
			IMFSourceReader item6 = iMFSourceReader;
			object item7 = instance;
			iMFSourceReader = null;
			instance = null;
			return (DeviceManager: textureNativeDeviceManager, Reader: item6, MediaSource: item7);
		}
		catch (Exception ex5)
		{
			MediaFoundationInterop.ReleaseComObject(iMFSourceReader);
			MediaFoundationInterop.ReleaseComObject(instance);
			textureNativeDeviceManager?.Dispose();
			throw new InvalidOperationException($"Texture-native camera reader unavailable. D3D12 path: {ex?.Message ?? "not attempted"} D3D11 bridge: {ex2?.Message ?? "not attempted"} System-memory NV12 upload: {ex5.Message}", ex5);
		}
	}

	private static (IMFSourceReader Reader, object MediaSource) CreateTextureSourceReader(CameraDevice camera, CameraVideoMode? mode, ITextureNativeDeviceManager deviceManager)
	{
		object mediaSource;
		return (Reader: MediaFoundationCameraDeviceFactory.CreateTextureSourceReader(camera, mode, deviceManager.Manager, out mediaSource, enableAdvancedVideoProcessing: true, MediaFoundationGuids.MFVideoFormat_NV12), MediaSource: mediaSource);
	}

	private static void ValidateTextureReader(CameraDevice camera, CameraVideoMode? mode, ITextureNativeDeviceManager deviceManager, string pathName)
	{
		object? instance = null;
		IMFSourceReader? iMFSourceReader = null;
		try
		{
			(iMFSourceReader, instance) = CreateTextureSourceReader(camera, mode, deviceManager);
			EnsureTextureReaderProducesSample(iMFSourceReader, pathName);
		}
		finally
		{
			MediaFoundationInterop.ReleaseComObject(iMFSourceReader);
			MediaFoundationInterop.ReleaseComObject(instance);
		}
	}

	private static void EnsureTextureReaderProducesSample(IMFSourceReader reader, string pathName)
	{
		DateTimeOffset dateTimeOffset = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2L);
		Exception? ex = null;
		while (DateTimeOffset.UtcNow < dateTimeOffset)
		{
			int actualStreamIndex;
			int streamFlags;
			long timestamp;
			object? sample;
			int num = reader.ReadSample(-4, 0, out actualStreamIndex, out streamFlags, out timestamp, out sample);
			try
			{
				if (MediaFoundationInterop.Failed(num))
				{
					ex = new InvalidOperationException($"{pathName} texture reader warmup failed: 0x{num:X8}");
					Thread.Sleep(25);
					continue;
				}
				if ((streamFlags & 2) != 0)
				{
					throw new InvalidOperationException(pathName + " texture reader ended during warmup.");
				}
				if (sample is IMFSample)
				{
					return;
				}
			}
			finally
			{
				MediaFoundationInterop.ReleaseComObject(sample);
			}
			Thread.Sleep(25);
		}
		throw ex ?? new InvalidOperationException(pathName + " texture reader produced no samples during warmup.");
	}

	private static TextureNativeRecordingResult CreateResult(bool success, string path, int samplesWritten, string deviceMode, Guid subtype, int width, int height, double fps, string status)
	{
		long bytesWritten = (File.Exists(path) ? new FileInfo(path).Length : 0);
		return new TextureNativeRecordingResult(success, path, samplesWritten, bytesWritten, deviceMode, MediaFoundationInterop.FormatSubtype(subtype), width, height, fps, status);
	}

	private static (int Width, int Height, double Fps, Guid Subtype) ReadCurrentFormat(IMFSourceReader reader, CameraVideoMode? requestedMode)
	{
		int item = requestedMode?.Width ?? 1280;
		int item2 = requestedMode?.Height ?? 720;
		double item3 = requestedMode?.FramesPerSecond ?? 30.0;
		Guid value = MediaFoundationGuids.MFVideoFormat_NV12;
		if (MediaFoundationInterop.Failed(reader.GetCurrentMediaType(-4, out IMFMediaType mediaType)))
		{
			return (Width: item, Height: item2, Fps: item3, Subtype: value);
		}
		try
		{
			if (MediaFoundationInterop.TryGetFrameSize(mediaType, out var width, out var height))
			{
				item = width;
				item2 = height;
			}
			if (MediaFoundationInterop.TryGetFrameRate(mediaType, out var framesPerSecond))
			{
				item3 = framesPerSecond;
			}
			mediaType.GetGUID(in MediaFoundationGuids.MF_MT_SUBTYPE, out value);
			return (Width: item, Height: item2, Fps: item3, Subtype: value);
		}
		finally
		{
			MediaFoundationInterop.ReleaseComObject(mediaType);
		}
	}
}
