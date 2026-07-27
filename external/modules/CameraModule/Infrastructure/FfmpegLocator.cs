using System;
using System.IO;
using System.Linq;

namespace AvatarBuilder.Modules.Infrastructure;

public static class FfmpegLocator
{
	public static string? FindFfmpeg()
	{
		string text = Path.Combine(AppContext.BaseDirectory, "dependencies");
		string text2 = Path.Combine(text, "ffmpeg", "win-x64", "ffmpeg.exe");
		if (File.Exists(text2))
		{
			return text2;
		}
		if (!Directory.Exists(text))
		{
			return null;
		}
		try
		{
			return (from path in Directory.EnumerateFiles(text, "ffmpeg.exe", SearchOption.AllDirectories)
				orderby path.Length
				select path).FirstOrDefault();
		}
		catch
		{
			return null;
		}
	}
}
