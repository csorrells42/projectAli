using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AvatarBuilder.Modules.Webcam.Common;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

public sealed class MediaFoundationCameraModeService
{
	private static readonly double[] CommonFrameRates = new double[5] { 24.0, 25.0, 30.0, 50.0, 60.0 };

	public bool IsAvailable => OperatingSystem.IsWindows();

	public Task<IReadOnlyList<CameraVideoMode>> GetModesAsync(CameraDevice camera, CancellationToken cancellationToken)
	{
		return Task.Run(delegate
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!OperatingSystem.IsWindows())
			{
				return [CameraVideoMode.Auto];
			}
			using (MediaFoundationCameraDeviceFactory.Startup())
			{
		object? mediaSource = null;
		IMFSourceReader? iMFSourceReader = null;
				try
				{
					iMFSourceReader = MediaFoundationCameraDeviceFactory.CreateModeProbeReader(camera, out mediaSource);
					List<CameraVideoMode> list = new List<CameraVideoMode> { CameraVideoMode.Auto };
					for (int i = 0; i < 512; i++)
					{
						cancellationToken.ThrowIfCancellationRequested();
						IMFMediaType mediaType;
						int nativeMediaType = iMFSourceReader.GetNativeMediaType(-4, i, out mediaType);
						if (nativeMediaType == -1072875847)
						{
							break;
						}
						if (!MediaFoundationInterop.Failed(nativeMediaType))
						{
							try
							{
								if (TryCreateMode(mediaType, out CameraVideoMode mode))
								{
									list.Add(mode);
								}
							}
							finally
							{
								MediaFoundationInterop.ReleaseComObject(mediaType);
							}
						}
					}
					return SortModes(AddFallbackModes(camera.Name, list));
				}
				catch
				{
					return SortModes(AddFallbackModes(camera.Name, [CameraVideoMode.Auto]));
				}
				finally
				{
					MediaFoundationInterop.ReleaseComObject(iMFSourceReader);
					MediaFoundationInterop.ReleaseComObject(mediaSource);
				}
			}
		}, cancellationToken);
	}

	private static bool TryCreateMode(IMFMediaType mediaType, out CameraVideoMode mode)
	{
		mode = CameraVideoMode.Auto;
		if (MediaFoundationInterop.Failed(mediaType.GetGUID(in MediaFoundationGuids.MF_MT_MAJOR_TYPE, out var value)) || value != MediaFoundationGuids.MFMediaType_Video || !MediaFoundationInterop.TryGetFrameSize(mediaType, out var width, out var height) || !MediaFoundationInterop.TryGetFrameRate(mediaType, out var framesPerSecond))
		{
			return false;
		}
		mediaType.GetGUID(in MediaFoundationGuids.MF_MT_SUBTYPE, out var value2);
		framesPerSecond = NormalizeFrameRate(framesPerSecond);
		string? text = ((value2 == Guid.Empty) ? null : NormalizeFormat(MediaFoundationInterop.FormatSubtype(value2)));
		string label = ((text == null) ? $"{width}x{height} @ {framesPerSecond:0.###} fps" : $"{width}x{height} @ {framesPerSecond:0.###} fps ({text.ToUpperInvariant()})");
		mode = new CameraVideoMode(label, width, height, framesPerSecond, text);
		return true;
	}

	private static IReadOnlyList<CameraVideoMode> SortModes(IReadOnlyList<CameraVideoMode> modes)
	{
		return (from mode in modes
			group mode by $"{mode.Width}|{mode.Height}|{mode.FramesPerSecond:0.###}" into @group
			select (from mode in @group
				orderby FormatPriority(mode.InputFormat), mode.InputFormat
				select mode).First() into mode
			orderby mode.IsAuto ? (-1) : 0, mode.Width.GetValueOrDefault() * mode.Height.GetValueOrDefault() descending, mode.Width.GetValueOrDefault() descending, mode.Height.GetValueOrDefault() descending, mode.FramesPerSecond.GetValueOrDefault() descending
			select mode).ToList();
	}

	private static double NormalizeFrameRate(double frameRate)
	{
		double[] commonFrameRates = CommonFrameRates;
		foreach (double num in commonFrameRates)
		{
			if (Math.Abs(frameRate - num) < 0.25)
			{
				return num;
			}
		}
		return Math.Round(frameRate, 3);
	}

	private static string? NormalizeFormat(string? format)
	{
		string? text = format?.ToLowerInvariant();
		if (!string.IsNullOrEmpty(text))
		{
			if (text == "mjpg")
			{
				return "mjpeg";
			}
			return text;
		}
		return null;
	}

	private static int FormatPriority(string? format)
	{
		return format?.ToLowerInvariant() switch
		{
			"mjpeg" => 0, 
			"mjpg" => 0, 
			"h264" => 1, 
			"nv12" => 2, 
			"rgb32" => 3, 
			null => 4, 
			_ => 5, 
		};
	}

	private static IReadOnlyList<CameraVideoMode> AddFallbackModes(string cameraName, IReadOnlyList<CameraVideoMode> existingModes)
	{
		if (!cameraName.Contains("insta360", StringComparison.OrdinalIgnoreCase) && !cameraName.Contains("link 2", StringComparison.OrdinalIgnoreCase))
		{
			return existingModes;
		}
		List<CameraVideoMode> list = existingModes.ToList();
		HashSet<string> hashSet = list.Select(CreateModeKey).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (CameraVideoMode item in CreateInsta360Link2FallbackModes())
		{
			if (hashSet.Add(CreateModeKey(item)))
			{
				list.Add(item);
			}
		}
		return list;
	}

	private static string CreateModeKey(CameraVideoMode mode)
	{
		return $"{mode.Width}|{mode.Height}|{mode.FramesPerSecond:0.###}";
	}

	private static IEnumerable<CameraVideoMode> CreateInsta360Link2FallbackModes()
	{
		(int, int, double)[] array = new(int, int, double)[5]
		{
			(3840, 2160, 30.0),
			(1920, 1440, 60.0),
			(1920, 1080, 60.0),
			(1280, 960, 60.0),
			(1280, 720, 60.0)
		};
		(int Width, int Height, double MaxFps)[] array2 = array;
		for (int i = 0; i < array2.Length; i++)
		{
			(int Width, int Height, double MaxFps) size = array2[i];
			foreach (double item in CommonFrameRates.Where((double fps) => fps <= size.MaxFps))
			{
				string label = $"{size.Width}x{size.Height} @ {item:0.###} fps (MJPEG)";
				yield return new CameraVideoMode(label, size.Width, size.Height, item, "mjpeg");
			}
		}
	}
}
