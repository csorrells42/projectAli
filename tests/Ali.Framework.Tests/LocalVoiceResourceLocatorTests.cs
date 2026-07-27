using Ali.Modules.Voice;

namespace Ali.Framework.Tests;

public sealed class LocalVoiceResourceLocatorTests
{
    [Fact]
    public void FindPythonExecutable_UsesBundledPortableRuntimeWithoutAVirtualEnvironment()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliPortablePythonTests", Guid.NewGuid().ToString("N"));
        var python = Path.Combine(root, "runtime", "python", "python.exe");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(python)!);
            File.WriteAllBytes(python, []);

            Assert.Equal(Path.GetFullPath(python), LocalVoiceResourceLocator.FindPythonExecutable(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
