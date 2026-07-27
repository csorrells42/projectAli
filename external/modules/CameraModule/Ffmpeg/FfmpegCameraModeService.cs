using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AvatarBuilder.Modules.Infrastructure;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.MediaFoundation;

namespace AvatarBuilder.Modules.Webcam.Ffmpeg;

public sealed class FfmpegCameraModeService
{
	private static readonly double[] CommonFrameRates = new double[5] { 24.0, 25.0, 30.0, 50.0, 60.0 };

	private static readonly Regex DirectShowRangeModeRegex = new Regex("(?:(?<kind>vcodec|pixel_format)=(?<format>[^\\s]+).*?)?min\\s+s=(?<minWidth>\\d+)x(?<minHeight>\\d+)\\s+fps=(?<minFps>\\d+(?:\\.\\d+)?).*?max\\s+s=(?<maxWidth>\\d+)x(?<maxHeight>\\d+)\\s+fps=(?<maxFps>\\d+(?:\\.\\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex DirectShowExactModeRegex = new Regex("(?:(?<kind>vcodec|pixel_format)=(?<format>[^\\s]+).*?)?\\bs=(?<width>\\d+)x(?<height>\\d+).*?fps=(?<fps>\\d+(?:\\.\\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private readonly string? _ffmpegPath = FfmpegLocator.FindFfmpeg();

	private readonly MediaFoundationCameraModeService _mediaFoundationModeService = new MediaFoundationCameraModeService();

	public bool IsAvailable
	{
		get
		{
			if (_ffmpegPath == null)
			{
				return _mediaFoundationModeService.IsAvailable;
			}
			return true;
		}
	}

	public async Task<IReadOnlyList<CameraVideoMode>> GetModesAsync(CameraDevice camera, CancellationToken cancellationToken)
	{
		List<CameraVideoMode> modes = new List<CameraVideoMode> { CameraVideoMode.Auto };
		if (_mediaFoundationModeService.IsAvailable && !string.Equals(camera.Source, "DirectShow", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				modes.AddRange((await _mediaFoundationModeService.GetModesAsync(camera, cancellationToken)).Where((CameraVideoMode mode) => !mode.IsAuto));
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
			}
		}
		string name = camera.DirectShowDeviceOrSelf().Name;
		if (_ffmpegPath == null)
		{
			return SortAndFallback(camera.Name, modes);
		}
		ProcessStartInfo processStartInfo = new ProcessStartInfo
		{
			FileName = _ffmpegPath,
			UseShellExecute = false,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			CreateNoWindow = true
		};
		processStartInfo.ArgumentList.Add("-hide_banner");
		processStartInfo.ArgumentList.Add("-list_options");
		processStartInfo.ArgumentList.Add("true");
		processStartInfo.ArgumentList.Add("-f");
		processStartInfo.ArgumentList.Add("dshow");
		processStartInfo.ArgumentList.Add("-i");
		processStartInfo.ArgumentList.Add("video=" + name);
			Process? process = null;
		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(8L));
			process = Process.Start(processStartInfo);
			if (process == null)
			{
				return SortAndFallback(camera.Name, modes);
			}
			string stderr = await process.StandardError.ReadToEndAsync(timeout.Token);
			await process.WaitForExitAsync(timeout.Token);
			modes.AddRange(ParseModes(stderr));
		}
		catch
		{
			return SortAndFallback(camera.Name, modes);
		}
		finally
		{
			KillProcessIfRunning(process);
			process?.Dispose();
		}
		return SortAndFallback(camera.Name, modes);
	}

	private static IReadOnlyList<CameraVideoMode> SortAndFallback(string cameraName, IReadOnlyList<CameraVideoMode> modes)
	{
		List<CameraVideoMode> list = (from mode in modes
			group mode by $"{mode.Width}|{mode.Height}|{mode.FramesPerSecond:0.###}" into @group
			select (from mode in @group
				orderby FormatPriority(mode.InputFormat), mode.InputFormat
				select mode).First() into mode
			orderby mode.IsAuto ? (-1) : 0, mode.Width.GetValueOrDefault() * mode.Height.GetValueOrDefault() descending, mode.Width.GetValueOrDefault() descending, mode.Height.GetValueOrDefault() descending, mode.FramesPerSecond.GetValueOrDefault() descending
			select mode).ToList();
		if (list.Count <= 1)
		{
			return AddFallbackModes(cameraName, list);
		}
		return list;
	}

	private static string CreateModeKey(CameraVideoMode mode)
	{
		return $"{mode.Width}|{mode.Height}|{mode.FramesPerSecond:0.###}";
	}

	private static IEnumerable<CameraVideoMode> ParseModes(string output)
	{
		string[] array = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
		foreach (string text in array)
		{
			if (text.Contains("(", StringComparison.Ordinal))
			{
				continue;
			}
			Match match = DirectShowRangeModeRegex.Match(text);
			if (match.Success)
			{
				foreach (CameraVideoMode item in ParseRangeMode(match))
				{
					yield return item;
				}
				continue;
			}
			Match match2 = DirectShowExactModeRegex.Match(text);
			if (match2.Success)
			{
				int width = int.Parse(match2.Groups["width"].Value, CultureInfo.InvariantCulture);
				int height = int.Parse(match2.Groups["height"].Value, CultureInfo.InvariantCulture);
				double fps = NormalizeFrameRate(double.Parse(match2.Groups["fps"].Value, CultureInfo.InvariantCulture));
				string? format = (match2.Groups["format"].Success ? match2.Groups["format"].Value : null);
				yield return CreateMode(width, height, fps, format);
			}
		}
	}

	private static IEnumerable<CameraVideoMode> ParseRangeMode(Match match)
	{
		int minWidth = int.Parse(match.Groups["minWidth"].Value, CultureInfo.InvariantCulture);
		int minHeight = int.Parse(match.Groups["minHeight"].Value, CultureInfo.InvariantCulture);
		int num = int.Parse(match.Groups["maxWidth"].Value, CultureInfo.InvariantCulture);
		int num2 = int.Parse(match.Groups["maxHeight"].Value, CultureInfo.InvariantCulture);
		if (minWidth != num || minHeight != num2)
		{
			yield break;
		}
		double minFps = double.Parse(match.Groups["minFps"].Value, CultureInfo.InvariantCulture);
		double maxFps = double.Parse(match.Groups["maxFps"].Value, CultureInfo.InvariantCulture);
		string? format = (match.Groups["format"].Success ? match.Groups["format"].Value : null);
		foreach (double item in CommonFrameRates.Where((double fps) => fps >= minFps - 0.01 && fps <= maxFps + 0.25))
		{
			yield return CreateMode(minWidth, minHeight, item, format);
		}
	}

	private static CameraVideoMode CreateMode(int width, int height, double fps, string? format)
	{
		string? text = NormalizeFormat(format);
		return new CameraVideoMode((text == null) ? $"{width}x{height} @ {fps:0.###} fps" : $"{width}x{height} @ {fps:0.###} fps ({text.ToUpperInvariant()})", width, height, fps, text);
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
			null => 1, 
			"h264" => 2, 
			_ => 3, 
		};
	}

	private static void KillProcessIfRunning(Process? process)
	{
		if (process == null)
		{
			return;
		}
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				process.WaitForExit(1500);
			}
		}
		catch
		{
		}
	}

	private static IReadOnlyList<CameraVideoMode> AddFallbackModes(string cameraName, IReadOnlyList<CameraVideoMode> existingModes)
	{
		if (!cameraName.Contains("insta360", StringComparison.OrdinalIgnoreCase) && !cameraName.Contains("link 2", StringComparison.OrdinalIgnoreCase))
		{
			return existingModes;
		}
		List<CameraVideoMode> list = existingModes.ToList();
		HashSet<string> hashSet = list.Select(CreateModeKey).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (CameraVideoMode item in CreateInsta360Link2FallbackModes(cameraName.Contains("virtual", StringComparison.OrdinalIgnoreCase) ? "yuyv422" : "mjpeg"))
		{
			if (hashSet.Add(CreateModeKey(item)))
			{
				list.Add(item);
			}
		}
		return (from mode in list
			orderby mode.IsAuto ? (-1) : 0, mode.Width.GetValueOrDefault() * mode.Height.GetValueOrDefault() descending, mode.Width.GetValueOrDefault() descending, mode.Height.GetValueOrDefault() descending, mode.FramesPerSecond.GetValueOrDefault() descending
			select mode).ToList();
	}

	private static IEnumerable<CameraVideoMode> CreateInsta360Link2FallbackModes(string inputFormat)
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
				yield return CreateMode(size.Width, size.Height, item, inputFormat);
			}
		}
	}
}
