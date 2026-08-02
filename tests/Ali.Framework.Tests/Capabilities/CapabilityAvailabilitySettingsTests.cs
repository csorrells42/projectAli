using Ali.Modules.Capabilities;

namespace Ali.Framework.Tests.Capabilities;

public sealed class CapabilityAvailabilitySettingsTests
{
    [Fact]
    public void Revision_IsAnImmutableSha256OfExactSortedSelections()
    {
        var source = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["z-unshown"] = true,
            [CapabilityGroupIds.Python] = false,
            ["a-unshown"] = false
        };
        var first = new CapabilityAvailabilitySettings(source);
        var sameSelectionsDifferentOrder = new CapabilityAvailabilitySettings(
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["a-unshown"] = false,
                ["z-unshown"] = true,
                [CapabilityGroupIds.Python] = false
            });

        source[CapabilityGroupIds.Python] = true;

        Assert.Equal(64, first.Revision.Length);
        Assert.Equal(32, Convert.FromHexString(first.Revision).Length);
        Assert.Equal(first.Revision, sameSelectionsDifferentOrder.Revision);
        Assert.False(first.IsEnabled(CapabilityGroupIds.Python));
        Assert.NotEqual(
            first.Revision,
            first.WithGroupSelection(CapabilityGroupIds.Python, true).Revision);
    }

    [Fact]
    public void EditsAndPresets_PreserveUnknownSelectionsAndRemainAdditive()
    {
        var settings = new CapabilityAvailabilitySettings(
                new Dictionary<string, bool>(StringComparer.Ordinal)
                {
                    ["future-hidden-group"] = true,
                    [CapabilityGroupIds.CSharpDotNetRoslyn] = false,
                    [CapabilityGroupIds.Python] = false
                })
            .WithGroupSelection(CapabilityGroupIds.Java, false)
            .ApplyPreset(CapabilityPresetIds.CSharp);

        Assert.True(settings.IsEnabled("future-hidden-group"));
        Assert.True(settings.IsEnabled(CapabilityGroupIds.CSharpDotNetRoslyn));
        Assert.True(settings.IsEnabled(CapabilityGroupIds.FilesAndArchives));
        Assert.True(settings.IsEnabled(CapabilityGroupIds.ProgrammingCore));
        Assert.False(settings.IsEnabled(CapabilityGroupIds.Python));
        Assert.False(settings.IsEnabled(CapabilityGroupIds.Java));
    }

    [Fact]
    public void MissingFile_ReturnsExplicitCurrentDefaults()
    {
        using var directory = new TemporaryDirectory();

        var result = CapabilityAvailabilitySettingsStore.Load(directory.Path);

        Assert.True(result.Success);
        Assert.Equal(CapabilityAvailabilityLoadStatus.MissingFileDefaults, result.Status);
        Assert.Null(result.Error);
        Assert.All(
            CanonicalCapabilityCatalog.Groups,
            group => Assert.Equal(group.EnabledByDefault, result.Settings.IsEnabled(group.Id)));
    }

    [Fact]
    public void SaveAndLoad_RoundTripOnlyTheCurrentFormatAndExactSelections()
    {
        using var directory = new TemporaryDirectory();
        var settings = new CapabilityAvailabilitySettings(
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["z-unshown"] = true,
                [CapabilityGroupIds.FilesAndArchives] = false,
                ["a-unshown"] = false
            });

        var initial = CapabilityAvailabilitySettingsStore.Load(directory.Path).Settings;
        var firstSave = CapabilityAvailabilitySettingsStore.Save(
            directory.Path,
            initial.Revision,
            settings.WithGroupSelection(CapabilityGroupIds.FilesAndArchives, true));
        Assert.True(firstSave.Success);
        var saved = CapabilityAvailabilitySettingsStore.Save(
            directory.Path,
            firstSave.Settings!.Revision,
            settings);
        var result = CapabilityAvailabilitySettingsStore.Load(directory.Path);
        var json = File.ReadAllText(CapabilityAvailabilitySettingsStore.GetSettingsPath(directory.Path));

        Assert.True(result.Success);
        Assert.Equal(CapabilityAvailabilityLoadStatus.Loaded, result.Status);
        Assert.True(saved.Success);
        Assert.Equal(settings.GroupSelections, saved.Settings!.GroupSelections);
        Assert.Equal(settings.GroupSelections, result.Settings.GroupSelections);
        Assert.Equal(settings.Revision, saved.Settings.Revision);
        Assert.Equal(settings.Revision, result.Settings.Revision);
        Assert.Contains("\"groupSelections\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("revision", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("schema", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            json.IndexOf("a-unshown", StringComparison.Ordinal)
            < json.IndexOf("z-unshown", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(CapabilityAvailabilitySettingsStore.GetSettingsPath(directory.Path))!,
            "*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Save_RejectsAStaleExpectedRevisionWithoutOverwritingNewerSelections()
    {
        using var directory = new TemporaryDirectory();
        var initial = CapabilityAvailabilitySettingsStore.Load(directory.Path).Settings;
        var newer = initial.WithGroupSelection(CapabilityGroupIds.Python, false);
        var first = CapabilityAvailabilitySettingsStore.Save(
            directory.Path,
            initial.Revision,
            newer);

        var staleWrite = CapabilityAvailabilitySettingsStore.Save(
            directory.Path,
            initial.Revision,
            initial.WithGroupSelection(CapabilityGroupIds.Java, false));
        var loaded = CapabilityAvailabilitySettingsStore.Load(directory.Path);

        Assert.True(first.Success);
        Assert.False(staleWrite.Success);
        Assert.Equal(CapabilityAvailabilitySaveStatus.Conflict, staleWrite.Status);
        Assert.Equal(newer.Revision, staleWrite.Settings!.Revision);
        Assert.Equal(newer.Revision, loaded.Settings.Revision);
    }

    [Fact]
    public void Save_WhenWriterLockIsHeld_ReturnsBusyWithoutWriting()
    {
        using var directory = new TemporaryDirectory();
        var initial = CapabilityAvailabilitySettingsStore.Load(directory.Path).Settings;
        var persisted = initial.WithGroupSelection(CapabilityGroupIds.Python, false);
        var first = CapabilityAvailabilitySettingsStore.Save(
            directory.Path,
            initial.Revision,
            persisted);
        Assert.True(first.Success);

        var path = CapabilityAvailabilitySettingsStore.GetSettingsPath(directory.Path);
        var before = File.ReadAllText(path);
        var lockPath = Path.Combine(Path.GetDirectoryName(path)!, ".capability-availability.lock");
        using (new FileStream(
                   lockPath,
                   FileMode.OpenOrCreate,
                   FileAccess.ReadWrite,
                   FileShare.None,
                   bufferSize: 1,
                   FileOptions.DeleteOnClose))
        {
            var result = CapabilityAvailabilitySettingsStore.Save(
                directory.Path,
                first.Settings!.Revision,
                persisted.WithGroupSelection(CapabilityGroupIds.Java, false));

            Assert.False(result.Success);
            Assert.Equal(CapabilityAvailabilitySaveStatus.Busy, result.Status);
            Assert.Null(result.Settings);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
            Assert.Equal(before, File.ReadAllText(path));
        }
    }

    [Fact]
    public async Task ConcurrentSaves_WithTheSameExpectedRevision_SerializeToOneWinner()
    {
        using var directory = new TemporaryDirectory();
        var initial = CapabilityAvailabilitySettingsStore.Load(directory.Path).Settings;
        var persisted = CapabilityAvailabilitySettingsStore.Save(
            directory.Path,
            initial.Revision,
            initial);
        Assert.True(persisted.Success);

        var expectedRevision = persisted.Settings!.Revision;
        var pythonDisabled = persisted.Settings.WithGroupSelection(CapabilityGroupIds.Python, false);
        var javaDisabled = persisted.Settings.WithGroupSelection(CapabilityGroupIds.Java, false);
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim(false);

        Task<CapabilityAvailabilitySaveResult> StartWriter(CapabilityAvailabilitySettings settings) =>
            Task.Run(() =>
            {
                ready.Signal();
                start.Wait();
                return CapabilityAvailabilitySettingsStore.Save(
                    directory.Path,
                    expectedRevision,
                    settings);
            });

        var firstWriter = StartWriter(pythonDisabled);
        var secondWriter = StartWriter(javaDisabled);
        var bothReady = ready.Wait(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        start.Set();
        Assert.True(bothReady);
        var results = await Task.WhenAll(firstWriter, secondWriter)
            .WaitAsync(TestContext.Current.CancellationToken);

        var winner = Assert.Single(
            results,
            result => result.Status == CapabilityAvailabilitySaveStatus.Saved);
        var loser = Assert.Single(
            results,
            result => result.Status != CapabilityAvailabilitySaveStatus.Saved);
        Assert.True(
            loser.Status is CapabilityAvailabilitySaveStatus.Busy or CapabilityAvailabilitySaveStatus.Conflict);
        Assert.NotNull(winner.Settings);
        Assert.Contains(winner.Settings.Revision, new[] { pythonDisabled.Revision, javaDisabled.Revision });

        var loaded = CapabilityAvailabilitySettingsStore.Load(directory.Path);
        Assert.True(loaded.Success);
        Assert.Equal(winner.Settings.Revision, loaded.Settings.Revision);
    }

    [Fact]
    public void AtomicSaveFailure_PreservesThePreviousFileAndRemovesTemporaryOutput()
    {
        using var directory = new TemporaryDirectory();
        var initial = CapabilityAvailabilitySettings.CreateDefault()
            .WithGroupSelection(CapabilityGroupIds.Python, false);
        var defaults = CapabilityAvailabilitySettingsStore.Load(directory.Path).Settings;
        var saved = CapabilityAvailabilitySettingsStore.Save(
            directory.Path,
            defaults.Revision,
            initial);
        Assert.True(saved.Success);
        var path = CapabilityAvailabilitySettingsStore.GetSettingsPath(directory.Path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(
                () => CapabilityAvailabilitySettingsStore.Save(
                    directory.Path,
                    initial.Revision,
                    initial.WithGroupSelection(CapabilityGroupIds.Java, false)));
        }

        var loaded = CapabilityAvailabilitySettingsStore.Load(directory.Path);
        Assert.Equal(initial.Revision, loaded.Settings.Revision);
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Save_DoesNotOverwriteFailedClosedState()
    {
        using var directory = new TemporaryDirectory();
        var path = CapabilityAvailabilitySettingsStore.GetSettingsPath(directory.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not-json");
        var failedLoad = CapabilityAvailabilitySettingsStore.Load(directory.Path);

        var save = CapabilityAvailabilitySettingsStore.Save(
            directory.Path,
            failedLoad.Settings.Revision,
            CapabilityAvailabilitySettings.CreateDefault());

        Assert.False(save.Success);
        Assert.Equal(CapabilityAvailabilitySaveStatus.FailedClosed, save.Status);
        Assert.Equal("not-json", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"groupSelections\":null}")]
    [InlineData("{\"groupSelections\":{\"python\":\"yes\"}}")]
    [InlineData("{\"revision\":37,\"groupSelections\":{\"python\":false}}")]
    public void CorruptOrNonCurrentFormat_FailsClosedWithAnExplicitError(string content)
    {
        using var directory = new TemporaryDirectory();
        var path = CapabilityAvailabilitySettingsStore.GetSettingsPath(directory.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        var result = CapabilityAvailabilitySettingsStore.Load(directory.Path);

        Assert.False(result.Success);
        Assert.Equal(CapabilityAvailabilityLoadStatus.FailedClosed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.All(
            CanonicalCapabilityCatalog.Groups,
            group => Assert.False(result.Settings.IsEnabled(group.Id)));
    }

    [Fact]
    public void UnreadableSavedPath_FailsClosedInsteadOfCreatingDefaults()
    {
        using var directory = new TemporaryDirectory();
        var path = CapabilityAvailabilitySettingsStore.GetSettingsPath(directory.Path);
        Directory.CreateDirectory(path);

        var result = CapabilityAvailabilitySettingsStore.Load(directory.Path);

        Assert.False(result.Success);
        Assert.Equal(CapabilityAvailabilityLoadStatus.FailedClosed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.All(
            CanonicalCapabilityCatalog.Groups,
            group => Assert.False(result.Settings.IsEnabled(group.Id)));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Ali-CapabilityAvailability-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
