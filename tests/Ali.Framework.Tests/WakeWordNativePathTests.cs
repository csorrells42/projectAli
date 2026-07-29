using AvatarBuilder.Modules.Audio.WakeWord;

namespace Ali.Framework.Tests;

public sealed class WakeWordNativePathTests
{
    [Fact]
    public async Task Load_StagesRequiredFilesWhenSherpaPathsExceedWindowsMaxPath()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "AliWakeWordNativePathTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(testRoot, "source");
        var cache = Path.Combine(Path.GetTempPath(), "AliWakeCache", Guid.NewGuid().ToString("N"));
        while (Path.Combine(source, WakeWordModelInfo.EncoderFileName).Length < 265)
        {
            source = Path.Combine(source, "long-model-segment");
        }

        Directory.CreateDirectory(source);
        try
        {
            foreach (var file in RequiredFileNames())
            {
                await File.WriteAllTextAsync(
                    Path.Combine(source, file),
                    "verified-" + file,
                    TestContext.Current.CancellationToken);
            }

            var result = WakeWordModelInfo.Load(source, cache);

            Assert.True(result.IsReady, result.Status);
            Assert.StartsWith(Path.GetFullPath(cache), result.ModelFolder, StringComparison.OrdinalIgnoreCase);
            Assert.All(RequiredPaths(result), path => Assert.True(path.Length < 260, path));
            Assert.Equal(
                await File.ReadAllTextAsync(
                    Path.Combine(source, WakeWordModelInfo.EncoderFileName),
                    TestContext.Current.CancellationToken),
                await File.ReadAllTextAsync(result.EncoderPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            try { Directory.Delete(testRoot, recursive: true); } catch { }
            try { Directory.Delete(cache, recursive: true); } catch { }
        }
    }

    private static IReadOnlyList<string> RequiredFileNames() =>
        [
            WakeWordModelInfo.EncoderFileName,
            WakeWordModelInfo.DecoderFileName,
            WakeWordModelInfo.JoinerFileName,
            "tokens.txt",
            "en.phone"
        ];

    private static IReadOnlyList<string> RequiredPaths(WakeWordModelInfo info) =>
        [
            info.EncoderPath,
            info.DecoderPath,
            info.JoinerPath,
            info.TokensPath,
            info.EnglishLexiconPath
        ];
}
