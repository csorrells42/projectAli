using System.Diagnostics;
using System.Runtime.InteropServices;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Coding.Infrastructure;

namespace Ali.Framework.Tests.Coding;

public sealed class AliBoundedProcessRunnerTests
{
    [Fact]
    public async Task Timeout_is_enforced_and_reported_without_requiring_caller_cancellation()
    {
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.True(File.Exists(executable));
        var elapsed = Stopwatch.StartNew();

        var result = await AliBoundedProcessRunner.RunAsync(
            executable,
            Environment.CurrentDirectory,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
            TimeSpan.FromMilliseconds(150),
            TestContext.Current.CancellationToken);

        elapsed.Stop();
        Assert.False(result.Success);
        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Caller_cancellation_remains_distinct_from_timeout()
    {
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AliBoundedProcessRunner.RunAsync(
                executable,
                Environment.CurrentDirectory,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
                TimeSpan.FromMinutes(1),
                cancellation.Token));
    }

    [Fact]
    public void Windows_installed_hard_linked_executable_has_a_stable_exact_fingerprint()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        var first = AliCodingExecutionAssetFingerprint.CaptureRequiredExecutable(
            executable,
            "The Windows-installed bounded runner test executable");
        var second = AliCodingExecutionAssetFingerprint.CaptureRequiredExecutable(
            executable,
            "The Windows-installed bounded runner test executable");

        Assert.Equal(first, second);
        Assert.StartsWith("file:sha256:", first.Identity, StringComparison.Ordinal);
    }

    [Fact]
    public void Selected_project_executable_with_a_hard_link_alias_remains_rejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var root = Path.Combine(
            TestRepository.Root,
            "bin",
            nameof(AliBoundedProcessRunnerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "selected-tool.exe");
            var alias = Path.Combine(root, "attacker-alias.exe");
            File.Copy(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "cmd.exe"),
                executable);
            Assert.True(
                CreateHardLinkW(alias, executable, IntPtr.Zero),
                "The adversarial executable hard link could not be created.");

            var exception = Assert.Throws<InvalidDataException>(() =>
                AliCodingExecutionAssetFingerprint.CaptureRequiredExecutable(
                    executable,
                    "The selected project executable"));

            Assert.Contains("hard-link alias", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Pinned_provider_root_accepts_only_its_complete_hard_link_alias_set()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var root = Path.Combine(
            TestRepository.Root,
            "bin",
            nameof(AliBoundedProcessRunnerTests),
            Guid.NewGuid().ToString("N"));
        var providerRoot = Path.Combine(root, "provider");
        var outsideRoot = Path.Combine(root, "outside");
        Directory.CreateDirectory(providerRoot);
        Directory.CreateDirectory(outsideRoot);
        try
        {
            var executable = Path.Combine(providerRoot, "provider-tool.exe");
            var internalAlias = Path.Combine(providerRoot, "provider-tool-alias.exe");
            var outsideAlias = Path.Combine(outsideRoot, "provider-tool-alias.exe");
            File.Copy(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "cmd.exe"),
                executable);
            Assert.True(
                CreateHardLinkW(internalAlias, executable, IntPtr.Zero),
                "The internal provider executable hard link could not be created.");

            var result = await AliBoundedProcessRunner.RunAsync(
                executable,
                providerRoot,
                ["/d", "/c", "echo pinned-provider-ran"],
                TimeSpan.FromSeconds(15),
                TestContext.Current.CancellationToken,
                environment: null,
                allowedExecutableHardLinkRoot: providerRoot);

            Assert.True(result.Success, result.Output);
            Assert.Contains("pinned-provider-ran", result.Output, StringComparison.Ordinal);

            Assert.True(
                CreateHardLinkW(outsideAlias, executable, IntPtr.Zero),
                "The external provider executable hard link could not be created.");
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                AliBoundedProcessRunner.RunAsync(
                    executable,
                    providerRoot,
                    ["/d", "/c", "exit 0"],
                    TimeSpan.FromSeconds(15),
                    TestContext.Current.CancellationToken,
                    environment: null,
                    allowedExecutableHardLinkRoot: providerRoot));

            Assert.Contains("leaves the pinned provider root", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Exact_executable_cannot_be_substituted_between_validation_and_process_start()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var root = Path.Combine(
            TestRepository.Root,
            "bin",
            nameof(AliBoundedProcessRunnerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var executable = Path.Combine(root, "authorized.exe");
        var substitute = Path.Combine(root, "substitute.exe");
        var displaced = Path.Combine(root, "displaced.exe");
        File.Copy(Path.Combine(system, "cmd.exe"), executable);
        File.Copy(Path.Combine(system, "where.exe"), substitute);
        var exact = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            executable,
            "The bounded runner test executable");
        var replacementBlocked = false;
        try
        {
            var result = await AliBoundedProcessRunner.RunAsync(
                exact,
                root,
                ["/d", "/c", "echo authorized-image-ran"],
                TimeSpan.FromSeconds(15),
                TestContext.Current.CancellationToken,
                () =>
                {
                    try
                    {
                        File.Move(executable, displaced);
                        File.Move(substitute, executable);
                    }
                    catch (IOException)
                    {
                        replacementBlocked = true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        replacementBlocked = true;
                    }
                });

            Assert.True(replacementBlocked);
            Assert.True(result.Success, result.Output);
            Assert.Contains("authorized-image-ran", result.Output, StringComparison.Ordinal);
            Assert.True(File.Exists(substitute));
            Assert.False(File.Exists(displaced));
        }
        finally
        {
            if (File.Exists(displaced) && !File.Exists(executable))
            {
                File.Move(displaced, executable);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
