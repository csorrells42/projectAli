using System;
using System.IO;
using System.Text;

namespace AvatarBuilder.Modules.Infrastructure;

public static class AtomicTextFileWriter
{
	public static void WriteAllText(string path, string contents, Encoding encoding)
	{
		string? obj = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
		Directory.CreateDirectory(obj);
		string text = Path.Combine(obj, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
		try
		{
			File.WriteAllText(text, contents, encoding);
			File.Move(text, path, overwrite: true);
		}
		catch
		{
			TryDelete(text);
			throw;
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}
}
