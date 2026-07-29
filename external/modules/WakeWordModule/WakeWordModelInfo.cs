using System.Security.Cryptography;
using System.Text;

namespace AvatarBuilder.Modules.Audio.WakeWord;

public sealed record WakeWordModelInfo(
	string ModelFolder,
	string EncoderPath,
	string DecoderPath,
	string JoinerPath,
	string TokensPath,
	string EnglishLexiconPath,
	bool IsReady,
	string Status)
{
	public const string ModelFolderName =
		"sherpa-onnx-kws-zipformer-zh-en-3M-2025-12-20";
	public const string EncoderFileName =
		"encoder-epoch-13-avg-2-chunk-8-left-64.int8.onnx";
	public const string DecoderFileName =
		"decoder-epoch-13-avg-2-chunk-8-left-64.onnx";
	public const string JoinerFileName =
		"joiner-epoch-13-avg-2-chunk-8-left-64.int8.onnx";

	public static WakeWordModelInfo Load(
		string? explicitFolder = null,
		string? nativeCacheRoot = null)
	{
		foreach (string candidate in CandidateFolders(explicitFolder))
		{
			WakeWordModelInfo info = FromFolder(
				Path.GetFullPath(candidate),
				"Sherpa wake-word model is incomplete");
			if (File.Exists(info.EncoderPath)
				&& File.Exists(info.DecoderPath)
				&& File.Exists(info.JoinerPath)
				&& File.Exists(info.TokensPath)
				&& File.Exists(info.EnglishLexiconPath))
			{
				WakeWordModelInfo ready = info with
					{
						IsReady = true,
						Status = "Sherpa dynamic wake-word model ready"
					};
				return RequiresNativePathCache(ready)
					? StageForNativePathCompatibility(ready, nativeCacheRoot)
					: ready;
			}
		}
		return new("", "", "", "", "", "", false,
			"Sherpa wake-word model is not installed");
	}

	private static WakeWordModelInfo FromFolder(string folder, string status) =>
		new(
			folder,
			Path.Combine(folder, EncoderFileName),
			Path.Combine(folder, DecoderFileName),
			Path.Combine(folder, JoinerFileName),
			Path.Combine(folder, "tokens.txt"),
			Path.Combine(folder, "en.phone"),
			false,
			status);

	private static bool RequiresNativePathCache(WakeWordModelInfo info) =>
		OperatingSystem.IsWindows()
		&& RequiredPaths(info).Any(path => path.Length >= 260);

	private static WakeWordModelInfo StageForNativePathCompatibility(
		WakeWordModelInfo source,
		string? nativeCacheRoot)
	{
		string cacheRoot = string.IsNullOrWhiteSpace(nativeCacheRoot)
			? Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Ali", "RuntimeCache", "WakeWord")
			: Path.GetFullPath(nativeCacheRoot);
		string fingerprint = ComputeModelFingerprint(source);
		string destinationFolder = Path.Combine(cacheRoot, fingerprint);
		Directory.CreateDirectory(destinationFolder);

		WakeWordModelInfo destination = FromFolder(
			destinationFolder,
			"Sherpa wake-word model is incomplete in the native path cache");
		foreach ((string sourcePath, string destinationPath) in
			RequiredPaths(source).Zip(RequiredPaths(destination)))
		{
			CopyVerified(sourcePath, destinationPath);
		}

		if (RequiredPaths(destination).Any(path => path.Length >= 260))
		{
			throw new PathTooLongException(
				"The configured wake-word runtime cache is still too long for Sherpa-ONNX. "
				+ "Choose a shorter native cache root.");
		}

		return destination with
		{
			IsReady = true,
			Status = "Sherpa dynamic wake-word model ready from the native path cache"
		};
	}

	private static IReadOnlyList<string> RequiredPaths(WakeWordModelInfo info) =>
		[
			info.EncoderPath,
			info.DecoderPath,
			info.JoinerPath,
			info.TokensPath,
			info.EnglishLexiconPath
		];

	private static void CopyVerified(string source, string destination)
	{
		var sourceInfo = new FileInfo(source);
		if (File.Exists(destination)
			&& new FileInfo(destination).Length == sourceInfo.Length
			&& GetSha256(destination).SequenceEqual(GetSha256(source)))
		{
			return;
		}

		string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.Copy(source, temporary, overwrite: false);
			File.Move(temporary, destination, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporary)) File.Delete(temporary);
		}

		if (new FileInfo(destination).Length != sourceInfo.Length
			|| !GetSha256(destination).SequenceEqual(GetSha256(source)))
		{
			throw new IOException("Wake-word native path cache verification failed: " + destination);
		}
	}

	private static string ComputeModelFingerprint(WakeWordModelInfo info)
	{
		string inventory = string.Join('|', RequiredPaths(info).Select(path =>
			Path.GetFileName(path) + ":" + Convert.ToHexString(GetSha256(path))));
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inventory)))[..16];
	}

	private static byte[] GetSha256(string path)
	{
		using var stream = new FileStream(
			path, FileMode.Open, FileAccess.Read, FileShare.Read,
			bufferSize: 64 * 1024, FileOptions.SequentialScan);
		return SHA256.HashData(stream);
	}

	private static IEnumerable<string> CandidateFolders(string? explicitFolder)
	{
		if (!string.IsNullOrWhiteSpace(explicitFolder))
		{
			yield return explicitFolder;
		}
		string? configured = Environment.GetEnvironmentVariable(
			"ALI_SHERPA_KWS_MODEL");
		if (!string.IsNullOrWhiteSpace(configured))
		{
			yield return configured;
		}
		yield return Path.Combine(AppContext.BaseDirectory,
			"dependencies", "audio", "keyword-spotting", ModelFolderName);
		yield return Path.Combine(AppContext.BaseDirectory,
			"models", ModelFolderName);
	}
}
