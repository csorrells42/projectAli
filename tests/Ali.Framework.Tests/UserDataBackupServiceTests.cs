using System.IO.Compression;
using Ali.Modules.Storage;

namespace Ali.Framework.Tests;

public sealed class UserDataBackupServiceTests
{
    [Fact]
    public void BackupAndRestore_RoundTripsAllPersistentDataAndPreservesExcludedTemporaryData()
    {
        using var workspace = new TemporaryDirectory();
        var dataRoot = Path.Combine(workspace.Path, "Data");
        var profileRoot = Path.Combine(workspace.Path, "Profile-Chris");
        var backupPath = Path.Combine(workspace.Path, "Backups", "verified.zip");

        Write(Path.Combine(dataRoot, "Conversations", "chat.json"), "original conversation");
        Write(Path.Combine(dataRoot, "Settings", "runtime.json"), "original settings");
        Write(Path.Combine(dataRoot, "Qdrant", "metadata.json"), "original index metadata");
        Write(Path.Combine(profileRoot, "Memory", "mem0.json"), "original memory");
        Write(Path.Combine(profileRoot, "GeneratedDocuments", "report.txt"), "original report");
        Write(Path.Combine(dataRoot, "SessionAudio", "temporary.wav"), "temporary audio");
        Write(Path.Combine(profileRoot, "SessionImages", "temporary.png"), "temporary image");

        var service = new UserDataBackupService(dataRoot, profileRoot);
        var created = service.CreateBackup(backupPath);

        Assert.True(File.Exists(backupPath));
        Assert.Equal(5, created.FileCount);
        Assert.True(created.TotalBytes > 0);
        var manifest = service.InspectBackup(backupPath);
        Assert.Equal(UserDataBackupService.ManifestVersion, manifest.Version);
        Assert.Contains("SessionAudio/", manifest.ExcludedPaths);
        Assert.Contains("SessionImages/", manifest.ExcludedPaths);

        using (var archive = ZipFile.OpenRead(backupPath))
        {
            var names = archive.Entries.Select(entry => entry.FullName).ToArray();
            Assert.Contains("data/Conversations/chat.json", names);
            Assert.Contains("data/Settings/runtime.json", names);
            Assert.Contains("data/Qdrant/metadata.json", names);
            Assert.Contains("profile/Memory/mem0.json", names);
            Assert.Contains("profile/GeneratedDocuments/report.txt", names);
            Assert.DoesNotContain(names, name => name.Contains("SessionAudio", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.Contains("SessionImages", StringComparison.OrdinalIgnoreCase));
        }

        Write(Path.Combine(dataRoot, "Conversations", "chat.json"), "damaged conversation");
        Write(Path.Combine(profileRoot, "Memory", "mem0.json"), "damaged memory");
        Write(Path.Combine(dataRoot, "stale.txt"), "must be removed");
        Write(Path.Combine(dataRoot, "SessionAudio", "temporary.wav"), "temporary audio must survive restore");

        var restored = service.RestoreBackup(backupPath);

        Assert.Equal(created.FileCount, restored.FileCount);
        Assert.Equal("original conversation", File.ReadAllText(Path.Combine(dataRoot, "Conversations", "chat.json")));
        Assert.Equal("original settings", File.ReadAllText(Path.Combine(dataRoot, "Settings", "runtime.json")));
        Assert.Equal("original index metadata", File.ReadAllText(Path.Combine(dataRoot, "Qdrant", "metadata.json")));
        Assert.Equal("original memory", File.ReadAllText(Path.Combine(profileRoot, "Memory", "mem0.json")));
        Assert.Equal("original report", File.ReadAllText(Path.Combine(profileRoot, "GeneratedDocuments", "report.txt")));
        Assert.False(File.Exists(Path.Combine(dataRoot, "stale.txt")));
        Assert.Equal("temporary audio must survive restore", File.ReadAllText(Path.Combine(dataRoot, "SessionAudio", "temporary.wav")));
    }

    [Fact]
    public void InspectBackup_RejectsZipWithoutAliManifest()
    {
        using var workspace = new TemporaryDirectory();
        var path = Path.Combine(workspace.Path, "not-an-ali-backup.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            archive.CreateEntry("random.txt");
        }

        var service = new UserDataBackupService(
            Path.Combine(workspace.Path, "Data"),
            Path.Combine(workspace.Path, "Profile"));

        var error = Assert.Throws<InvalidOperationException>(() => service.InspectBackup(path));
        Assert.Contains("manifest is missing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AliBackupTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup must not hide the behavior under test.
            }
        }
    }
}
