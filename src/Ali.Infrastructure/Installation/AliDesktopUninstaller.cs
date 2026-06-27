using System.Text.Json;

namespace Ali.Infrastructure.Installation;

public sealed record AliDesktopUninstallOptions(
    string LocalAliRoot,
    bool RemoveUserData = false)
{
    public static AliDesktopUninstallOptions CreateDefault() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ali"));
}

public sealed record AliDesktopUninstallResult(
    bool Succeeded,
    string Message,
    string LocalAliRoot,
    string ReceiptPath,
    IReadOnlyList<string> RemovedPaths,
    IReadOnlyList<string> PreservedPaths,
    IReadOnlyList<string> Warnings);

public sealed class AliDesktopUninstaller
{
    public async Task<AliDesktopUninstallResult> UninstallAsync(
        AliDesktopUninstallOptions options,
        CancellationToken cancellationToken = default)
    {
        var localRoot = string.IsNullOrWhiteSpace(options.LocalAliRoot)
            ? AliDesktopUninstallOptions.CreateDefault().LocalAliRoot
            : Path.GetFullPath(options.LocalAliRoot);
        var removedPaths = new List<string>();
        var preservedPaths = new List<string>();
        var warnings = new List<string>();
        var receiptPath = string.Empty;

        try
        {
            if (IsUnsafeLocalRoot(localRoot))
            {
                throw new InvalidOperationException($"Refusing to uninstall from unsafe Ali root: {localRoot}");
            }

            var devRun = Path.Combine(localRoot, "DevRun");
            DeleteDirectoryIfExists(devRun, removedPaths);
            DeleteShortcutIfExists(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Ali.lnk"),
                localRoot,
                removedPaths,
                warnings);
            DeleteShortcutIfExists(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Ali", "Ali.lnk"),
                localRoot,
                removedPaths,
                warnings);

            if (options.RemoveUserData)
            {
                DeleteDirectoryIfExists(localRoot, removedPaths);
            }
            else
            {
                AddPreservedIfExists(Path.Combine(localRoot, "BootstrapData"), preservedPaths);
                AddPreservedIfExists(Path.Combine(localRoot, "Profiles"), preservedPaths);
                AddPreservedIfExists(Path.Combine(localRoot, "Backups"), preservedPaths);
                AddPreservedIfExists(localRoot, preservedPaths);
            }

            receiptPath = CreateReceiptPath(localRoot, options.RemoveUserData, preservedPaths.Count > 0);
            await WriteReceiptAsync(
                    receiptPath,
                    localRoot,
                    options.RemoveUserData,
                    removedPaths,
                    preservedPaths,
                    warnings,
                    cancellationToken)
                .ConfigureAwait(false);

            var message = removedPaths.Count == 0 && preservedPaths.Count == 0
                ? "No Ali app files were found. Nothing was removed."
                : options.RemoveUserData
                ? "Ali app and user data were removed."
                : "Ali app was removed. User data was preserved.";
            return new AliDesktopUninstallResult(true, message, localRoot, receiptPath, removedPaths, preservedPaths, warnings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            warnings.Add(ex.Message);
            try
            {
                receiptPath = string.IsNullOrWhiteSpace(receiptPath)
                    ? CreateReceiptPath(localRoot, removeUserData: true, useLocalReceipt: false)
                    : receiptPath;
                await WriteReceiptAsync(
                        receiptPath,
                        localRoot,
                        options.RemoveUserData,
                        removedPaths,
                        preservedPaths,
                        warnings,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original uninstall failure even if receipt writing also fails.
            }

            return new AliDesktopUninstallResult(
                false,
                $"Ali uninstall failed: {ex.Message}",
                localRoot,
                receiptPath,
                removedPaths,
                preservedPaths,
                warnings);
        }
    }

    private static void DeleteDirectoryIfExists(string directory, List<string> removedPaths)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        Directory.Delete(directory, recursive: true);
        removedPaths.Add(directory);
    }

    private static void DeleteShortcutIfExists(
        string shortcutPath,
        string localRoot,
        List<string> removedPaths,
        List<string> warnings)
    {
        try
        {
            if (!File.Exists(shortcutPath))
            {
                return;
            }

            if (!ShortcutTargetsLocalRoot(shortcutPath, localRoot))
            {
                warnings.Add($"Shortcut was not removed because its target could not be verified under {localRoot}: {shortcutPath}");
                return;
            }

            File.Delete(shortcutPath);
            removedPaths.Add(shortcutPath);
            var parent = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrWhiteSpace(parent)
                && parent.EndsWith(Path.Combine("Programs", "Ali"), StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(parent)
                && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
                removedPaths.Add(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            warnings.Add($"Shortcut could not be removed at {shortcutPath}: {ex.Message}");
        }
    }

    private static bool ShortcutTargetsLocalRoot(string shortcutPath, string localRoot)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return false;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            string targetPath = shortcut.TargetPath;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return false;
            }

            var fullTarget = Path.GetFullPath(targetPath);
            var fullLocalRoot = Path.GetFullPath(localRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;
            return fullTarget.StartsWith(fullLocalRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void AddPreservedIfExists(string path, List<string> preservedPaths)
    {
        if ((Directory.Exists(path) || File.Exists(path))
            && !preservedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            preservedPaths.Add(path);
        }
    }

    public static bool IsUnsafeLocalRoot(string localRoot)
    {
        var fullPath = Path.GetFullPath(localRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pathRoot = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(fullPath)
            || string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var unsafeRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };

        return unsafeRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Any(path => string.Equals(fullPath, path, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateReceiptPath(string localRoot, bool removeUserData, bool useLocalReceipt)
    {
        var receiptsRoot = removeUserData || !useLocalReceipt
            ? Path.Combine(Path.GetTempPath(), "Ali.UninstallReceipts")
            : Path.Combine(localRoot, "BootstrapData", "install-receipts");
        return Path.Combine(receiptsRoot, $"uninstall-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
    }

    private static async Task WriteReceiptAsync(
        string receiptPath,
        string localRoot,
        bool removeUserData,
        IReadOnlyList<string> removedPaths,
        IReadOnlyList<string> preservedPaths,
        IReadOnlyList<string> warnings,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        var receipt = new
        {
            createdAt = DateTimeOffset.Now,
            localAliRoot = localRoot,
            removeUserData,
            removedPaths,
            preservedPaths,
            warnings
        };

        await using var stream = File.Create(receiptPath);
        await JsonSerializer.SerializeAsync(
                stream,
                receipt,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
