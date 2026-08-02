using System.Reflection;
using Ali.Modules.Capabilities;
using Ali.UI.ViewModels;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.Capabilities;

public sealed class CapabilitySettingsViewModelTests
{
    [Fact]
    public void InitialProjection_CopiesStableRowsPresetsCountsReasonsAndExactStatuses()
    {
        var files = Descriptor("files.read", "files_read", CapabilityGroupIds.FilesAndArchives);
        var core = Descriptor("core.inspect", "core_inspect", CapabilityGroupIds.ProgrammingCore);
        var csharpReady = Descriptor("csharp.ready", "csharp_ready", CapabilityGroupIds.CSharpDotNetRoslyn);
        var csharpMissing = Descriptor("csharp.missing", "csharp_missing", CapabilityGroupIds.CSharpDotNetRoslyn);
        var pythonMissing = Descriptor("python.missing", "python_missing", CapabilityGroupIds.Python);
        var settings = CapabilityAvailabilitySettings.CreateDefault()
            .WithGroupSelection(CapabilityGroupIds.FilesAndArchives, false);
        var fixture = CreateFixture(
            [files, core, csharpReady, csharpMissing, pythonMissing],
            [files, core, csharpReady],
            settings);

        var viewModel = CreateViewModel(fixture.Owner);
        var envelope = fixture.Owner.CaptureSettings();

        Assert.Equal(
            CanonicalCapabilityCatalog.Groups.Select(group => group.Id),
            viewModel.Rows.Select(row => row.GroupId));
        Assert.Equal(
            CanonicalCapabilityCatalog.Presets.Select(preset => preset.Id),
            viewModel.Presets.Select(preset => preset.Id));
        Assert.Equal("Disabled", Row(viewModel, CapabilityGroupIds.FilesAndArchives).Status);
        Assert.Equal("Ready", Row(viewModel, CapabilityGroupIds.ProgrammingCore).Status);
        Assert.Equal("Degraded", Row(viewModel, CapabilityGroupIds.CSharpDotNetRoslyn).Status);
        Assert.Equal("Unavailable", Row(viewModel, CapabilityGroupIds.Python).Status);
        Assert.Equal("Empty", Row(viewModel, CapabilityGroupIds.Java).Status);
        Assert.Equal(envelope.KnownGroupCount, viewModel.KnownGroupCount);
        Assert.Equal(envelope.EnabledGroupCount, viewModel.EnabledGroupCount);
        Assert.Equal(envelope.DisabledGroupCount, viewModel.DisabledGroupCount);
        Assert.Equal(envelope.DeclaredTaskToolCount, viewModel.DeclaredTaskToolCount);
        Assert.Equal(envelope.CallableTaskToolCount, viewModel.CallableTaskToolCount);
        Assert.Equal(envelope.UnavailableTaskToolCount, viewModel.UnavailableTaskToolCount);

        var sourceReason = Assert.Single(
            envelope.Rows.Single(row => row.GroupId == CapabilityGroupIds.Python).Reasons);
        var copiedReason = Assert.Single(Row(viewModel, CapabilityGroupIds.Python).Reasons);
        Assert.Equal(sourceReason.CapabilityId, copiedReason.CapabilityId);
        Assert.Equal(sourceReason.ToolName, copiedReason.ToolName);
        Assert.Equal(sourceReason.Code.ToString(), copiedReason.Code);
        Assert.Equal(sourceReason.DependencyId, copiedReason.DependencyId);
        Assert.Equal(sourceReason.Message, copiedReason.Message);
        Assert.NotEqual(sourceReason.GetType(), copiedReason.GetType());
    }

    [Fact]
    public void Draft_TogglingBackToAppliedValueClearsDirtyAndUpdatesPresetCounts()
    {
        var fixture = CreateFixture();
        var viewModel = CreateViewModel(fixture.Owner);
        var files = Row(viewModel, CapabilityGroupIds.FilesAndArchives);
        var filePreset = viewModel.Presets.Single(preset => preset.Id == CapabilityPresetIds.FileTools);

        Assert.False(viewModel.IsDirty);
        Assert.True(filePreset.IsFullyApplied);

        files.IsEnabled = false;

        Assert.True(viewModel.IsDirty);
        Assert.False(filePreset.IsFullyApplied);
        Assert.Equal(1, filePreset.WouldEnableGroupCount);

        files.IsEnabled = true;

        Assert.False(viewModel.IsDirty);
        Assert.True(filePreset.IsFullyApplied);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task MissingFileDefaults_AreSaveableWithoutAnEditAndNoChangeAppliesCurrent()
    {
        var persistence = new ControlledPersistence(CapabilityAvailabilitySettings.CreateDefault());
        persistence.SetLoadResult(
            CapabilityAvailabilityLoadResult.MissingFileDefaults(
                CapabilityAvailabilitySettings.CreateDefault()));
        var fixture = CreateFixture(persistence: persistence);
        var viewModel = CreateViewModel(fixture.Owner);

        Assert.True(viewModel.NeedsInitialSave);
        Assert.False(viewModel.IsDirty);
        Assert.True(viewModel.SaveCommand.CanExecute(null));

        await viewModel.SaveAsync();

        Assert.Equal(1, persistence.SaveCallCount);
        Assert.False(viewModel.NeedsInitialSave);
        Assert.False(viewModel.IsDirty);
        Assert.Equal("Loaded", viewModel.LoadStatus);
        Assert.Contains("already up to date", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_AppliesPublishedCurrentAndClearsTheDraft()
    {
        var fixture = CreateFixture();
        var viewModel = CreateViewModel(fixture.Owner);
        Row(viewModel, CapabilityGroupIds.Python).IsEnabled = false;

        await viewModel.SaveAsync();

        Assert.False(fixture.Persistence.CurrentSettings.IsEnabled(CapabilityGroupIds.Python));
        Assert.False(Row(viewModel, CapabilityGroupIds.Python).IsEnabled);
        Assert.False(viewModel.IsDirty);
        Assert.False(viewModel.RequiresReload);
        Assert.Contains("capability-availability.json", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavedChange_RefreshesRunningMcpPublicationExactlyOnce()
    {
        var fixture = CreateFixture();
        var refreshCount = 0;
        var viewModel = new CapabilitySettingsViewModel(
            fixture.Owner,
            @"C:\private\long\profile\capability-availability.json",
            () =>
            {
                refreshCount++;
                return Task.FromResult(true);
            });
        Row(viewModel, CapabilityGroupIds.Python).IsEnabled = false;

        await viewModel.SaveAsync();

        Assert.Equal(1, refreshCount);
        Assert.Contains("MCP server", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("saved capability set", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnchangedOrRejectedSave_DoesNotRefreshMcpPublication()
    {
        var fixture = CreateFixture();
        var refreshCount = 0;
        var viewModel = new CapabilitySettingsViewModel(
            fixture.Owner,
            @"C:\private\long\profile\capability-availability.json",
            () =>
            {
                refreshCount++;
                return Task.FromResult(true);
            });

        await viewModel.SaveAsync();
        Row(viewModel, CapabilityGroupIds.Python).IsEnabled = false;
        fixture.Persistence.PublishExternal(
            fixture.Persistence.CurrentSettings
                .WithGroupSelection(CapabilityGroupIds.Java, false));
        await viewModel.SaveAsync();

        Assert.Equal(0, refreshCount);
    }

    [Fact]
    public async Task PublicationRefreshFailure_PreservesTheSavedSettingsWithoutLeakingDetails()
    {
        var fixture = CreateFixture();
        var viewModel = new CapabilitySettingsViewModel(
            fixture.Owner,
            @"C:\private\long\profile\capability-availability.json",
            static () => throw new IOException(@"simulated C:\private\mcp failure"));
        Row(viewModel, CapabilityGroupIds.Python).IsEnabled = false;

        await viewModel.SaveAsync();

        Assert.False(fixture.Persistence.CurrentSettings.IsEnabled(CapabilityGroupIds.Python));
        Assert.False(viewModel.IsDirty);
        Assert.Contains("failed safely", viewModel.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\private", viewModel.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("simulated", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preset_IsAdditiveOverTheCurrentDraft()
    {
        var fixture = CreateFixture();
        var viewModel = CreateViewModel(fixture.Owner);
        Row(viewModel, CapabilityGroupIds.Python).IsEnabled = false;
        Row(viewModel, CapabilityGroupIds.FilesAndArchives).IsEnabled = false;
        Row(viewModel, CapabilityGroupIds.CSharpDotNetRoslyn).IsEnabled = false;
        viewModel.SelectedPreset = viewModel.Presets.Single(
            preset => preset.Id == CapabilityPresetIds.CSharp);

        await viewModel.ApplySelectedPresetAsync();

        Assert.True(fixture.Persistence.CurrentSettings.IsEnabled(CapabilityGroupIds.FilesAndArchives));
        Assert.True(fixture.Persistence.CurrentSettings.IsEnabled(CapabilityGroupIds.CSharpDotNetRoslyn));
        Assert.False(fixture.Persistence.CurrentSettings.IsEnabled(CapabilityGroupIds.Python));
        Assert.True(Row(viewModel, CapabilityGroupIds.FilesAndArchives).IsEnabled);
        Assert.True(Row(viewModel, CapabilityGroupIds.CSharpDotNetRoslyn).IsEnabled);
        Assert.False(Row(viewModel, CapabilityGroupIds.Python).IsEnabled);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public async Task Conflict_PreservesDraftBlocksMutationsAndReloadAppliesAuthoritativeCurrent()
    {
        var fixture = CreateFixture();
        var viewModel = CreateViewModel(fixture.Owner);
        Row(viewModel, CapabilityGroupIds.Python).IsEnabled = false;
        fixture.Persistence.PublishExternal(
            fixture.Persistence.CurrentSettings
                .WithGroupSelection(CapabilityGroupIds.Java, false));

        await viewModel.SaveAsync();

        Assert.True(viewModel.RequiresReload);
        Assert.False(viewModel.CanEdit);
        Assert.True(viewModel.IsDirty);
        Assert.False(Row(viewModel, CapabilityGroupIds.Python).IsEnabled);
        Assert.True(Row(viewModel, CapabilityGroupIds.Java).IsEnabled);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.ApplyPresetCommand.CanExecute(null));
        Assert.Contains("draft is preserved", viewModel.StatusText, StringComparison.Ordinal);

        await viewModel.ReloadAsync();

        Assert.False(viewModel.RequiresReload);
        Assert.True(viewModel.CanEdit);
        Assert.False(viewModel.IsDirty);
        Assert.True(Row(viewModel, CapabilityGroupIds.Python).IsEnabled);
        Assert.False(Row(viewModel, CapabilityGroupIds.Java).IsEnabled);
    }

    [Fact]
    public async Task BusyAndWriteFailed_PreserveRetryableDraftWithoutLeakingRawErrors()
    {
        var fixture = CreateFixture();
        var viewModel = CreateViewModel(fixture.Owner);
        Row(viewModel, CapabilityGroupIds.Python).IsEnabled = false;
        fixture.Persistence.SaveOverride = static (_, _) => CapabilityAvailabilitySaveResult.Busy();

        await viewModel.SaveAsync();

        Assert.True(viewModel.IsDirty);
        Assert.True(viewModel.CanEdit);
        Assert.False(Row(viewModel, CapabilityGroupIds.Python).IsEnabled);
        Assert.Contains("try saving again", viewModel.StatusText, StringComparison.Ordinal);

        fixture.Persistence.SaveOverride = static (_, _) =>
            throw new IOException(@"simulated C:\private\profile\capability-availability.json write failure");
        await viewModel.SaveAsync();

        Assert.True(viewModel.IsDirty);
        Assert.True(viewModel.CanEdit);
        Assert.False(Row(viewModel, CapabilityGroupIds.Python).IsEnabled);
        Assert.DoesNotContain(@"C:\private", viewModel.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("simulated", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("capability-availability.json", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidRequest_PreservesDisplayAndRequiresReload()
    {
        var fixture = CreateFixture();
        var viewModel = CreateViewModel(fixture.Owner);
        var removed = Row(viewModel, CapabilityGroupIds.Python);
        viewModel.Rows.Remove(removed);

        await viewModel.SaveAsync();

        Assert.True(viewModel.RequiresReload);
        Assert.False(viewModel.CanEdit);
        Assert.DoesNotContain(viewModel.Rows, row => row.GroupId == CapabilityGroupIds.Python);
        Assert.Contains("draft is preserved", viewModel.StatusText, StringComparison.Ordinal);

        await viewModel.ReloadAsync();

        Assert.Contains(viewModel.Rows, row => row.GroupId == CapabilityGroupIds.Python);
        Assert.False(viewModel.RequiresReload);
    }

    [Fact]
    public async Task FailedClosed_AppliesFailClosedCurrentAndBlocksEdits()
    {
        var fixture = CreateFixture();
        var viewModel = CreateViewModel(fixture.Owner);
        Row(viewModel, CapabilityGroupIds.Python).IsEnabled = false;
        fixture.Persistence.SaveOverride = static (_, settings) =>
            CapabilityAvailabilitySaveResult.FailedClosed(
                settings,
                @"simulated C:\private\profile\capability-availability.json failure");

        await viewModel.SaveAsync();

        Assert.True(viewModel.IsFailedClosed);
        Assert.True(viewModel.RequiresReload);
        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.IsDirty);
        Assert.All(viewModel.Rows, row => Assert.False(row.IsEnabled));
        Assert.All(viewModel.Rows, row => Assert.False(row.IsEditable));
        Assert.DoesNotContain(@"C:\private", viewModel.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("simulated", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void UserVisibleSettingsMessages_UseOnlyTheFileName()
    {
        const string settingsPath = @"C:\private\long\profile\capability-availability.json";
        var fixture = CreateFixture();

        var viewModel = new CapabilitySettingsViewModel(fixture.Owner, settingsPath);

        Assert.Equal("capability-availability.json", viewModel.SettingsFileName);
        Assert.Contains(viewModel.SettingsFileName, viewModel.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\private", viewModel.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(settingsPath, viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicPropertyGraph_IsCopiedDataOnlyAndDoesNotExposeExecutionObjects()
    {
        var graph = WalkPublicPropertyGraph(typeof(CapabilitySettingsViewModel));
        var properties = graph
            .Where(IsAliApplicationType)
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            .ToArray();

        Assert.Contains(typeof(CapabilitySettingsRowViewModel), graph);
        Assert.Contains(typeof(CapabilitySettingsPresetViewModel), graph);
        Assert.Contains(typeof(CapabilitySettingsReasonViewModel), graph);
        Assert.DoesNotContain(typeof(CapabilitySettingsSnapshotOwner), graph);
        Assert.DoesNotContain(typeof(CapabilitySettingsEnvelope), graph);
        Assert.DoesNotContain(typeof(CanonicalCapabilityRegistry), graph);
        Assert.DoesNotContain(typeof(CapabilityDescriptor), graph);
        Assert.DoesNotContain(typeof(CapabilitySchemaFactory), graph);
        Assert.DoesNotContain(graph, type => typeof(Delegate).IsAssignableFrom(type));
        Assert.DoesNotContain(graph, type => typeof(AITool).IsAssignableFrom(type));
        Assert.DoesNotContain(
            graph,
            type => type.Namespace?.StartsWith("Microsoft.Extensions.AI", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(
            properties,
            property => property.Name.Contains("Path", StringComparison.Ordinal)
                        && !string.Equals(property.Name, "SettingsFileName", StringComparison.Ordinal));
    }

    private static CapabilitySettingsViewModel CreateViewModel(
        CapabilitySettingsSnapshotOwner owner) =>
        new(owner, @"C:\private\long\profile\capability-availability.json");

    private static CapabilitySettingsRowViewModel Row(
        CapabilitySettingsViewModel viewModel,
        string groupId) =>
        viewModel.Rows.Single(row => string.Equals(row.GroupId, groupId, StringComparison.Ordinal));

    private static CapabilityDescriptor Descriptor(
        string id,
        string toolName,
        string groupId) =>
        CanonicalCapabilityRegistryTests.Descriptor(id, toolName, groupId);

    private static Fixture CreateFixture(
        IReadOnlyList<CapabilityDescriptor>? descriptors = null,
        IReadOnlyList<CapabilityDescriptor>? registeredDescriptors = null,
        CapabilityAvailabilitySettings? settings = null,
        ControlledPersistence? persistence = null)
    {
        var actualDescriptors = descriptors ??
        [
            Descriptor("files.read", "files_read", CapabilityGroupIds.FilesAndArchives),
            Descriptor("python.inspect", "python_inspect", CapabilityGroupIds.Python)
        ];
        var actualRegistered = registeredDescriptors ?? actualDescriptors;
        var registry = CanonicalCapabilityRegistryTests.Registry(actualDescriptors);
        var actualPersistence = persistence
            ?? new ControlledPersistence(settings ?? CapabilityAvailabilitySettings.CreateDefault());
        var runtime = Runtime(actualRegistered);
        var owner = new CapabilitySettingsSnapshotOwner(
            registry,
            new CapabilityResolver(),
            runtime,
            actualPersistence);
        return new Fixture(owner, actualPersistence);
    }

    private static CapabilityRuntimeAvailability Runtime(
        IReadOnlyList<CapabilityDescriptor> registeredDescriptors) =>
        new(
            "user-a",
            "runtime-1",
            registeredDescriptors.Select(descriptor => CapabilityRuntimeToolRegistration.Create(
                descriptor.SchemaFactory(),
                descriptor.SchemaFactoryId)),
            "providers-1",
            ["test-provider"],
            null,
            "permissions-1",
            ["test-permission"],
            "mcp-1",
            Array.Empty<string>(),
            Array.Empty<string>(),
            "reconcilers-1",
            Array.Empty<string>());

    private static IReadOnlySet<Type> WalkPublicPropertyGraph(Type root)
    {
        var visited = new HashSet<Type>();

        void Visit(Type type)
        {
            if (!visited.Add(type))
            {
                return;
            }
            if (type.IsArray)
            {
                Visit(type.GetElementType()!);
            }
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    Visit(argument);
                }
            }
            if (!IsAliApplicationType(type))
            {
                return;
            }

            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                Visit(property.PropertyType);
            }
        }

        Visit(root);
        return visited;
    }

    private static bool IsAliApplicationType(Type type) =>
        type.Assembly == typeof(CapabilitySettingsViewModel).Assembly;

    private sealed record Fixture(
        CapabilitySettingsSnapshotOwner Owner,
        ControlledPersistence Persistence);

    private sealed class ControlledPersistence : ICapabilityAvailabilitySettingsPersistence
    {
        private readonly object _gate = new();
        private CapabilityAvailabilitySettings _currentSettings;
        private CapabilityAvailabilityLoadResult? _loadOverride;
        private int _saveCallCount;

        public ControlledPersistence(CapabilityAvailabilitySettings currentSettings)
        {
            _currentSettings = currentSettings;
        }

        public Func<string, CapabilityAvailabilitySettings, CapabilityAvailabilitySaveResult>? SaveOverride { get; set; }

        public int SaveCallCount => Volatile.Read(ref _saveCallCount);

        public CapabilityAvailabilitySettings CurrentSettings
        {
            get
            {
                lock (_gate)
                {
                    return _currentSettings;
                }
            }
        }

        public CapabilityAvailabilityLoadResult Load()
        {
            lock (_gate)
            {
                return _loadOverride ?? CapabilityAvailabilityLoadResult.Loaded(_currentSettings);
            }
        }

        public CapabilityAvailabilitySaveResult Save(
            string expectedRevision,
            CapabilityAvailabilitySettings settings)
        {
            Interlocked.Increment(ref _saveCallCount);
            var saveOverride = SaveOverride;
            return saveOverride is null
                ? CompareExchangeSave(expectedRevision, settings)
                : saveOverride(expectedRevision, settings);
        }

        public void PublishExternal(CapabilityAvailabilitySettings settings)
        {
            lock (_gate)
            {
                _currentSettings = settings;
                _loadOverride = null;
            }
        }

        public void SetLoadResult(CapabilityAvailabilityLoadResult result)
        {
            lock (_gate)
            {
                _loadOverride = result;
            }
        }

        private CapabilityAvailabilitySaveResult CompareExchangeSave(
            string expectedRevision,
            CapabilityAvailabilitySettings settings)
        {
            lock (_gate)
            {
                if (!string.Equals(expectedRevision, _currentSettings.Revision, StringComparison.Ordinal))
                {
                    return CapabilityAvailabilitySaveResult.Conflict(_currentSettings);
                }

                _currentSettings = new CapabilityAvailabilitySettings(settings.GroupSelections);
                _loadOverride = null;
                return CapabilityAvailabilitySaveResult.Saved(_currentSettings);
            }
        }
    }
}
