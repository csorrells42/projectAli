using Ali.Modules.Capabilities;

namespace Ali.Framework.Tests.Capabilities;

public sealed class CapabilitySettingsSnapshotOwnerTests
{
    [Fact]
    public void CapturedSettingsAndPlanning_DescribeOneMatchingPublication()
    {
        var fixture = CreateFixture();

        AssertPublicationMatches(fixture.Owner);
    }

    [Fact]
    public void SaveRows_PersistsExactVisibleSelectionsAndPreservesExplicitUnknownValues()
    {
        var initial = CapabilityAvailabilitySettings.CreateDefault()
            .WithGroupSelection("future-disabled", false)
            .WithGroupSelection("future-enabled", true);
        var fixture = CreateFixture(initial);
        var before = fixture.Owner.CaptureSettings();
        var displayed = DisplayedSelections(before);
        displayed[CapabilityGroupIds.FilesAndArchives] = false;
        displayed[CapabilityGroupIds.Python] = false;

        var result = fixture.Owner.TrySaveRows(before.Stamp, displayed);

        Assert.Equal(CapabilitySettingsMutationStatus.Saved, result.Status);
        Assert.Equal(1, fixture.Persistence.SaveCallCount);
        Assert.Equal(initial.Revision, fixture.Persistence.LastExpectedRevision);
        var persisted = Assert.IsType<CapabilityAvailabilitySettings>(fixture.Persistence.LastRequestedSettings);
        var expected = initial.GroupSelections.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        foreach (var (groupId, enabled) in displayed)
        {
            expected[groupId] = enabled;
        }
        AssertSelectionsEqual(expected, persisted.GroupSelections);
        Assert.False(persisted.IsEnabled("future-disabled"));
        Assert.True(persisted.IsEnabled("future-enabled"));
        Assert.Contains(result.Current.UnknownSelections, item => item.GroupId == "future-disabled" && !item.Enabled);
        Assert.Contains(result.Current.UnknownSelections, item => item.GroupId == "future-enabled" && item.Enabled);
        AssertPublicationMatches(fixture.Owner);
    }

    [Fact]
    public void ApplyPreset_IsAdditiveOverTheDisplayedDraftAndPreservesUnknownValues()
    {
        var initial = CapabilityAvailabilitySettings.CreateDefault()
            .WithGroupSelection("future-disabled", false)
            .WithGroupSelection("future-enabled", true);
        var fixture = CreateFixture(initial);
        var before = fixture.Owner.CaptureSettings();
        var draft = DisplayedSelections(before);
        draft[CapabilityGroupIds.FilesAndArchives] = false;
        draft[CapabilityGroupIds.ProgrammingCore] = false;
        draft[CapabilityGroupIds.CSharpDotNetRoslyn] = false;
        draft[CapabilityGroupIds.DevOpsArchitectureQuality] = false;
        draft[CapabilityGroupIds.Python] = false;
        draft[CapabilityGroupIds.Java] = false;
        draft[CapabilityGroupIds.VisualStudio] = false;

        var result = fixture.Owner.TryApplyPreset(
            before.Stamp,
            CapabilityPresetIds.CSharp,
            draft);

        Assert.Equal(CapabilitySettingsMutationStatus.Saved, result.Status);
        var persisted = fixture.Persistence.CurrentSettings;
        Assert.True(persisted.IsEnabled(CapabilityGroupIds.FilesAndArchives));
        Assert.True(persisted.IsEnabled(CapabilityGroupIds.ProgrammingCore));
        Assert.True(persisted.IsEnabled(CapabilityGroupIds.CSharpDotNetRoslyn));
        Assert.True(persisted.IsEnabled(CapabilityGroupIds.DevOpsArchitectureQuality));
        Assert.False(persisted.IsEnabled(CapabilityGroupIds.Python));
        Assert.False(persisted.IsEnabled(CapabilityGroupIds.Java));
        Assert.False(persisted.IsEnabled(CapabilityGroupIds.VisualStudio));
        Assert.False(persisted.IsEnabled("future-disabled"));
        Assert.True(persisted.IsEnabled("future-enabled"));
        AssertPublicationMatches(fixture.Owner);
    }

    [Fact]
    public void InvalidDisplayedRowSet_DoesNotPersistOrPublish()
    {
        var fixture = CreateFixture();
        var beforeEnvelope = fixture.Owner.CaptureSettings();
        var beforePlanning = fixture.Owner.CapturePlanning();
        var missing = DisplayedSelections(beforeEnvelope);
        missing.Remove(CapabilityGroupIds.Python);

        var missingResult = fixture.Owner.TrySaveRows(beforeEnvelope.Stamp, missing);

        var extra = DisplayedSelections(beforeEnvelope);
        extra["not-a-visible-row"] = true;
        var extraResult = fixture.Owner.TryApplyPreset(
            beforeEnvelope.Stamp,
            CapabilityPresetIds.CSharp,
            extra);

        Assert.Equal(CapabilitySettingsMutationStatus.InvalidRequest, missingResult.Status);
        Assert.Equal(CapabilitySettingsMutationStatus.InvalidRequest, extraResult.Status);
        Assert.Equal(0, fixture.Persistence.SaveCallCount);
        Assert.Same(beforeEnvelope, missingResult.Current);
        Assert.Same(beforeEnvelope, extraResult.Current);
        Assert.Same(beforeEnvelope, fixture.Owner.CaptureSettings());
        Assert.Same(beforePlanning, fixture.Owner.CapturePlanning());
    }

    [Fact]
    public void StaleStamp_RejectsSavePresetAndNoOpBeforePersistence()
    {
        var fixture = CreateFixture();
        var stale = fixture.Owner.CaptureSettings();
        var firstEdit = DisplayedSelections(stale);
        firstEdit[CapabilityGroupIds.Python] = false;
        var firstSave = fixture.Owner.TrySaveRows(stale.Stamp, firstEdit);
        Assert.Equal(CapabilitySettingsMutationStatus.Saved, firstSave.Status);
        var current = fixture.Owner.CaptureSettings();
        var changed = DisplayedSelections(current);
        changed[CapabilityGroupIds.Java] = false;
        var noOp = DisplayedSelections(current);

        var staleSave = fixture.Owner.TrySaveRows(stale.Stamp, changed);
        var stalePreset = fixture.Owner.TryApplyPreset(
            stale.Stamp,
            CapabilityPresetIds.CSharp,
            changed);
        var staleNoOp = fixture.Owner.TrySaveRows(stale.Stamp, noOp);

        Assert.Equal(CapabilitySettingsMutationStatus.Conflict, staleSave.Status);
        Assert.Equal(CapabilitySettingsMutationStatus.Conflict, stalePreset.Status);
        Assert.Equal(CapabilitySettingsMutationStatus.Conflict, staleNoOp.Status);
        Assert.Equal(1, fixture.Persistence.SaveCallCount);
        Assert.Same(current, staleSave.Current);
        Assert.Same(current, stalePreset.Current);
        Assert.Same(current, staleNoOp.Current);
        Assert.Same(current, fixture.Owner.CaptureSettings());
    }

    [Fact]
    public void NoOp_StillUsesPersistenceCasAndDetectsExternalConflict()
    {
        var fixture = CreateFixture();
        var before = fixture.Owner.CaptureSettings();
        var external = fixture.Persistence.CurrentSettings
            .WithGroupSelection(CapabilityGroupIds.Java, false);
        fixture.Persistence.PublishExternal(external);

        var result = fixture.Owner.TrySaveRows(before.Stamp, DisplayedSelections(before));

        Assert.Equal(CapabilitySettingsMutationStatus.Conflict, result.Status);
        Assert.Equal(1, fixture.Persistence.SaveCallCount);
        Assert.Equal(external.Revision, result.Current.SettingsRevision);
        Assert.Equal(external.Revision, fixture.Owner.CapturePlanning().Settings.Revision);
        Assert.False(fixture.Owner.CapturePlanning().Settings.IsEnabled(CapabilityGroupIds.Java));
        AssertPublicationMatches(fixture.Owner);
    }

    [Fact]
    public void NoOp_SaveUsesPersistenceCasAndPublishesThePersistedSnapshot()
    {
        var fixture = CreateFixture();
        var before = fixture.Owner.CaptureSettings();

        var result = fixture.Owner.TrySaveRows(before.Stamp, DisplayedSelections(before));

        Assert.Equal(CapabilitySettingsMutationStatus.NoChange, result.Status);
        Assert.Equal(1, fixture.Persistence.SaveCallCount);
        Assert.Equal(before.SettingsRevision, fixture.Persistence.LastExpectedRevision);
        Assert.NotSame(before, result.Current);
        Assert.Equal(before.SettingsRevision, result.Current.SettingsRevision);
        Assert.NotEqual(before.PublicationRevision, result.Current.PublicationRevision);
        AssertPublicationMatches(fixture.Owner);
    }

    [Fact]
    public void NoOpPreset_StillUsesPersistenceCasAndDetectsExternalConflict()
    {
        var fixture = CreateFixture();
        var before = fixture.Owner.CaptureSettings();
        var external = fixture.Persistence.CurrentSettings
            .WithGroupSelection(CapabilityGroupIds.Java, false);
        fixture.Persistence.PublishExternal(external);

        var result = fixture.Owner.TryApplyPreset(
            before.Stamp,
            CapabilityPresetIds.CSharp,
            DisplayedSelections(before));

        Assert.Equal(CapabilitySettingsMutationStatus.Conflict, result.Status);
        Assert.Equal(1, fixture.Persistence.SaveCallCount);
        Assert.Equal(external.Revision, result.Current.SettingsRevision);
        Assert.False(fixture.Owner.CapturePlanning().Settings.IsEnabled(CapabilityGroupIds.Java));
        AssertPublicationMatches(fixture.Owner);
    }

    [Fact]
    public void DiskConflict_RepublishesAuthoritativeSettingsInsteadOfTheDraft()
    {
        var fixture = CreateFixture();
        var before = fixture.Owner.CaptureSettings();
        var external = fixture.Persistence.CurrentSettings
            .WithGroupSelection(CapabilityGroupIds.RaspberryPi, false);
        fixture.Persistence.PublishExternal(external);
        var draft = DisplayedSelections(before);
        draft[CapabilityGroupIds.Python] = false;

        var result = fixture.Owner.TrySaveRows(before.Stamp, draft);

        Assert.Equal(CapabilitySettingsMutationStatus.Conflict, result.Status);
        Assert.Equal(external.Revision, result.Current.SettingsRevision);
        var planning = fixture.Owner.CapturePlanning();
        Assert.Equal(external.Revision, planning.Settings.Revision);
        Assert.False(planning.Settings.IsEnabled(CapabilityGroupIds.RaspberryPi));
        Assert.True(planning.Settings.IsEnabled(CapabilityGroupIds.Python));
        AssertPublicationMatches(fixture.Owner);
    }

    [Fact]
    public void BusyAndWriteException_PublishNothing()
    {
        var fixture = CreateFixture();
        var beforeEnvelope = fixture.Owner.CaptureSettings();
        var beforePlanning = fixture.Owner.CapturePlanning();
        var draft = DisplayedSelections(beforeEnvelope);
        draft[CapabilityGroupIds.Python] = false;
        fixture.Persistence.SaveOverride = static (_, _) => CapabilityAvailabilitySaveResult.Busy();

        var busy = fixture.Owner.TrySaveRows(beforeEnvelope.Stamp, draft);

        fixture.Persistence.SaveOverride = static (_, _) => throw new IOException("simulated write failure");
        var failed = fixture.Owner.TrySaveRows(beforeEnvelope.Stamp, draft);

        Assert.Equal(CapabilitySettingsMutationStatus.Busy, busy.Status);
        Assert.Equal(CapabilitySettingsMutationStatus.WriteFailed, failed.Status);
        Assert.Contains("IOException", failed.Error, StringComparison.Ordinal);
        Assert.Equal(2, fixture.Persistence.SaveCallCount);
        Assert.Same(beforeEnvelope, busy.Current);
        Assert.Same(beforeEnvelope, failed.Current);
        Assert.Same(beforeEnvelope, fixture.Owner.CaptureSettings());
        Assert.Same(beforePlanning, fixture.Owner.CapturePlanning());
    }

    [Fact]
    public void FailedLoadAndFailedSave_PublishFailClosedSettings()
    {
        var failedLoadPersistence = new ControlledPersistence(CapabilityAvailabilitySettings.CreateDefault());
        failedLoadPersistence.SetLoadResult(CapabilityAvailabilityLoadResult.FailedClosed("simulated load failure"));
        var failedLoadFixture = CreateFixture(persistence: failedLoadPersistence);

        var loadedEnvelope = failedLoadFixture.Owner.CaptureSettings();
        Assert.Equal(CapabilityAvailabilityLoadStatus.FailedClosed, loadedEnvelope.LoadStatus);
        Assert.Equal("simulated load failure", loadedEnvelope.LoadError);
        Assert.All(loadedEnvelope.Rows, row => Assert.False(row.Enabled));
        Assert.All(
            failedLoadFixture.Owner.CapturePlanning().Settings.GroupSelections,
            selection => Assert.False(selection.Value));
        AssertPublicationMatches(failedLoadFixture.Owner);

        var failedSaveFixture = CreateFixture();
        var before = failedSaveFixture.Owner.CaptureSettings();
        var draft = DisplayedSelections(before);
        draft[CapabilityGroupIds.Python] = false;
        failedSaveFixture.Persistence.SaveOverride = static (_, settings) =>
            CapabilityAvailabilitySaveResult.FailedClosed(settings, "simulated save failure");

        var save = failedSaveFixture.Owner.TrySaveRows(before.Stamp, draft);

        Assert.Equal(CapabilitySettingsMutationStatus.FailedClosed, save.Status);
        Assert.Equal(CapabilityAvailabilityLoadStatus.FailedClosed, save.Current.LoadStatus);
        Assert.Equal("simulated save failure", save.Current.LoadError);
        Assert.All(save.Current.Rows, row => Assert.False(row.Enabled));
        Assert.All(
            failedSaveFixture.Owner.CapturePlanning().Settings.GroupSelections,
            selection => Assert.False(selection.Value));
        AssertPublicationMatches(failedSaveFixture.Owner);
    }

    [Fact]
    public void Reload_PublishesExternalSettingsAsOneNewSnapshot()
    {
        var fixture = CreateFixture();
        var before = fixture.Owner.CaptureSettings();
        var external = fixture.Persistence.CurrentSettings
            .WithGroupSelection(CapabilityGroupIds.Python, false)
            .WithGroupSelection("external-future-group", true);
        fixture.Persistence.PublishExternal(external);

        var reloaded = fixture.Owner.Reload();

        Assert.NotSame(before, reloaded);
        Assert.Equal(CapabilityAvailabilityLoadStatus.Loaded, reloaded.LoadStatus);
        Assert.Equal(external.Revision, reloaded.SettingsRevision);
        Assert.Contains(
            reloaded.UnknownSelections,
            item => item.GroupId == "external-future-group" && item.Enabled);
        Assert.Equal(0, fixture.Persistence.SaveCallCount);
        Assert.Equal(external.Revision, fixture.Owner.CapturePlanning().Settings.Revision);
        AssertPublicationMatches(fixture.Owner);
    }

    [Fact]
    public void RuntimeRefresh_UsesPublicationCasAndRejectsAStaleRuntimeUpdate()
    {
        var fixture = CreateFixture();
        var first = fixture.Owner.CaptureSettings();
        var settingsRevision = first.SettingsRevision;
        var refreshedRuntime = Runtime(fixture.Descriptors, "2");

        var refreshed = fixture.Owner.TryPublishRuntime(
            first.Stamp,
            refreshedRuntime,
            out var refreshedEnvelope);
        var stale = fixture.Owner.TryPublishRuntime(
            first.Stamp,
            Runtime(fixture.Descriptors, "3"),
            out var currentEnvelope);

        Assert.True(refreshed);
        Assert.False(stale);
        Assert.Equal(settingsRevision, refreshedEnvelope.SettingsRevision);
        Assert.Equal(refreshedRuntime.RuntimeRevision, refreshedEnvelope.RuntimeRevision);
        Assert.NotEqual(first.ResolutionRevision, refreshedEnvelope.ResolutionRevision);
        Assert.Same(refreshedEnvelope, currentEnvelope);
        Assert.Same(refreshedEnvelope, fixture.Owner.CaptureSettings());
        Assert.Same(refreshedRuntime, fixture.Owner.CapturePlanning().Runtime);
        Assert.Equal(0, fixture.Persistence.SaveCallCount);
        AssertPublicationMatches(fixture.Owner);
    }

    [Fact]
    public void RuntimeRefreshWinningFirst_RejectsTheStaleSaveBeforePersistence()
    {
        var fixture = CreateFixture();
        var before = fixture.Owner.CaptureSettings();
        var refreshedRuntime = Runtime(fixture.Descriptors, "runtime-first");
        var draft = DisplayedSelections(before);
        draft[CapabilityGroupIds.Python] = false;

        var published = fixture.Owner.TryPublishRuntime(
            before.Stamp,
            refreshedRuntime,
            out var refreshedEnvelope);
        var staleSave = fixture.Owner.TrySaveRows(before.Stamp, draft);

        Assert.True(published);
        Assert.Equal(CapabilitySettingsMutationStatus.Conflict, staleSave.Status);
        Assert.Equal(0, fixture.Persistence.SaveCallCount);
        Assert.Same(refreshedEnvelope, staleSave.Current);
        Assert.Same(refreshedRuntime, fixture.Owner.CapturePlanning().Runtime);
        Assert.True(fixture.Owner.CapturePlanning().Settings.IsEnabled(CapabilityGroupIds.Python));
        AssertPublicationMatches(fixture.Owner);
    }

    [Fact]
    public void TargetRefresh_WithStableOuterRevisions_RejectsAnOlderPublication()
    {
        var fixture = CreateFixture();
        var before = fixture.Owner.CaptureSettings();
        var newerTarget = new CapabilityTargetResolution(
            "binding-new",
            "test-provider",
            "target-resolution-new",
            "providers-1");
        var olderTarget = new CapabilityTargetResolution(
            "binding-old",
            "test-provider",
            "target-resolution-old",
            "providers-1");
        var newerRuntime = Runtime(fixture.Descriptors, "1", newerTarget);
        var olderRuntime = Runtime(fixture.Descriptors, "1", olderTarget);

        var published = fixture.Owner.TryPublishRuntime(
            before.Stamp,
            newerRuntime,
            out var newerEnvelope);
        var stalePublished = fixture.Owner.TryPublishRuntime(
            before.Stamp,
            olderRuntime,
            out var currentEnvelope);

        Assert.True(published);
        Assert.False(stalePublished);
        Assert.Equal(before.ResolutionRevision, newerEnvelope.ResolutionRevision);
        Assert.NotEqual(before.PublicationRevision, newerEnvelope.PublicationRevision);
        Assert.Same(newerEnvelope, currentEnvelope);
        Assert.Same(newerRuntime, fixture.Owner.CapturePlanning().Runtime);
        Assert.Same(newerTarget, fixture.Owner.CapturePlanning().Runtime.TargetResolution);
        AssertPublicationMatches(fixture.Owner);
    }

    [Fact]
    public async Task ConcurrentSaves_WithOneStamp_HaveExactlyOneWinner()
    {
        var fixture = CreateFixture();
        var before = fixture.Owner.CaptureSettings();
        var pythonDraft = DisplayedSelections(before);
        pythonDraft[CapabilityGroupIds.Python] = false;
        var javaDraft = DisplayedSelections(before);
        javaDraft[CapabilityGroupIds.Java] = false;
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim(false);
        var cancellationToken = TestContext.Current.CancellationToken;

        Task<CapabilitySettingsMutationResult> StartWriter(IReadOnlyDictionary<string, bool> draft) =>
            Task.Run(
                () =>
                {
                    ready.Signal();
                    start.Wait(cancellationToken);
                    return fixture.Owner.TrySaveRows(before.Stamp, draft);
                },
                cancellationToken);

        var first = StartWriter(pythonDraft);
        var second = StartWriter(javaDraft);
        var bothReady = ready.Wait(TimeSpan.FromSeconds(10), cancellationToken);
        start.Set();
        Assert.True(bothReady);
        var results = await Task.WhenAll(first, second).WaitAsync(cancellationToken);

        Assert.Single(results, result => result.Status == CapabilitySettingsMutationStatus.Saved);
        Assert.Single(results, result => result.Status == CapabilitySettingsMutationStatus.Conflict);
        Assert.Equal(1, fixture.Persistence.SaveCallCount);
        Assert.Contains(
            fixture.Persistence.CurrentSettings.Revision,
            new[]
            {
                SettingsFromDraft(before, pythonDraft).Revision,
                SettingsFromDraft(before, javaDraft).Revision
            });
        AssertPublicationMatches(fixture.Owner);
    }

    [Fact]
    public async Task SaveHoldingWriterGate_BlocksRuntimeRefreshAndPublishesOneConsistentWinner()
    {
        var fixture = CreateFixture();
        var beforeEnvelope = fixture.Owner.CaptureSettings();
        var beforePlanning = fixture.Owner.CapturePlanning();
        var draft = DisplayedSelections(beforeEnvelope);
        draft[CapabilityGroupIds.Python] = false;
        var refreshedRuntime = Runtime(fixture.Descriptors, "race");
        using var saveEntered = new ManualResetEventSlim(false);
        using var releaseSave = new ManualResetEventSlim(false);
        using var runtimeStarted = new ManualResetEventSlim(false);
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Persistence.SaveOverride = (expectedRevision, settings) =>
        {
            saveEntered.Set();
            releaseSave.Wait(cancellationToken);
            return fixture.Persistence.CompareExchangeSave(expectedRevision, settings);
        };

        var saveTask = Task.Run(
            () => fixture.Owner.TrySaveRows(beforeEnvelope.Stamp, draft),
            cancellationToken);
        var entered = saveEntered.Wait(TimeSpan.FromSeconds(10), cancellationToken);
        if (!entered)
        {
            releaseSave.Set();
        }
        Assert.True(entered);

        var runtimeTask = Task.Run(
            () =>
            {
                runtimeStarted.Set();
                var published = fixture.Owner.TryPublishRuntime(
                    beforeEnvelope.Stamp,
                    refreshedRuntime,
                    out var envelope);
                return (Published: published, Envelope: envelope);
            },
            cancellationToken);
        bool attempted;
        try
        {
            attempted = runtimeStarted.Wait(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Same(beforeEnvelope, fixture.Owner.CaptureSettings());
            Assert.Same(beforePlanning, fixture.Owner.CapturePlanning());
            Assert.True(attempted);
        }
        finally
        {
            releaseSave.Set();
        }

        var save = await saveTask.WaitAsync(cancellationToken);
        var runtime = await runtimeTask.WaitAsync(cancellationToken);

        Assert.Equal(CapabilitySettingsMutationStatus.Saved, save.Status);
        Assert.False(runtime.Published);
        Assert.Same(save.Current, runtime.Envelope);
        var finalPlanning = fixture.Owner.CapturePlanning();
        Assert.Equal(save.Current.SettingsRevision, finalPlanning.Settings.Revision);
        Assert.Same(beforePlanning.Runtime, finalPlanning.Runtime);
        Assert.NotEqual(refreshedRuntime.RuntimeRevision, finalPlanning.Runtime.RuntimeRevision);
        AssertPublicationMatches(fixture.Owner);
    }

    private static OwnerFixture CreateFixture(
        CapabilityAvailabilitySettings? initialSettings = null,
        ControlledPersistence? persistence = null)
    {
        var descriptors = new[]
        {
            CanonicalCapabilityRegistryTests.Descriptor(
                "files.read",
                "file_access_read",
                CapabilityGroupIds.FilesAndArchives),
            CanonicalCapabilityRegistryTests.Descriptor(
                "coding.inspect",
                "coding_inspect_project",
                CapabilityGroupIds.ProgrammingCore)
        };
        var registry = CanonicalCapabilityRegistryTests.Registry(descriptors);
        var actualPersistence = persistence
            ?? new ControlledPersistence(initialSettings ?? CapabilityAvailabilitySettings.CreateDefault());
        var initialRuntime = Runtime(descriptors, "1");
        var owner = new CapabilitySettingsSnapshotOwner(
            registry,
            new CapabilityResolver(),
            initialRuntime,
            actualPersistence);
        return new OwnerFixture(owner, actualPersistence, descriptors);
    }

    private static CapabilityRuntimeAvailability Runtime(
        IReadOnlyList<CapabilityDescriptor> descriptors,
        string revisionSuffix,
        CapabilityTargetResolution? targetResolution = null) =>
        new(
            "user-a",
            $"runtime-{revisionSuffix}",
            descriptors.Select(descriptor => CapabilityRuntimeToolRegistration.Create(
                descriptor.SchemaFactory(),
                descriptor.SchemaFactoryId)),
            $"providers-{revisionSuffix}",
            descriptors.Select(descriptor => descriptor.ProviderId).Distinct(StringComparer.Ordinal),
            targetResolution,
            $"permissions-{revisionSuffix}",
            descriptors.Select(descriptor => descriptor.Permission.PolicyId).Distinct(StringComparer.Ordinal),
            $"mcp-{revisionSuffix}",
            Array.Empty<string>(),
            descriptors
                .Where(descriptor => descriptor.McpExposure.Exposed)
                .Select(descriptor => descriptor.ToolName),
            $"reconcilers-{revisionSuffix}",
            descriptors
                .Where(descriptor => descriptor.Effect.ReconcilerId is not null)
                .Select(descriptor => descriptor.Effect.ReconcilerId!)
                .Distinct(StringComparer.Ordinal));

    private static Dictionary<string, bool> DisplayedSelections(CapabilitySettingsEnvelope envelope) =>
        envelope.Rows.ToDictionary(row => row.GroupId, row => row.Enabled, StringComparer.Ordinal);

    private static CapabilityAvailabilitySettings SettingsFromDraft(
        CapabilitySettingsEnvelope before,
        IReadOnlyDictionary<string, bool> draft)
    {
        var selections = before.Rows.ToDictionary(row => row.GroupId, row => row.Enabled, StringComparer.Ordinal);
        foreach (var (groupId, enabled) in draft)
        {
            selections[groupId] = enabled;
        }
        return new CapabilityAvailabilitySettings(selections);
    }

    private static void AssertPublicationMatches(CapabilitySettingsSnapshotOwner owner)
    {
        var envelope = owner.CaptureSettings();
        var planning = owner.CapturePlanning();
        Assert.Equal(planning.Settings.Revision, planning.Registry.SettingsRevision);
        Assert.Equal(planning.Registry.RegistryRevision, planning.Resolution.RegistryRevision);
        Assert.Equal(planning.Registry.SettingsRevision, planning.Resolution.SettingsRevision);
        Assert.Equal(planning.Runtime.RuntimeRevision, planning.Resolution.RuntimeRevision);
        Assert.Equal(envelope.RegistryRevision, planning.Registry.RegistryRevision);
        Assert.Equal(envelope.SettingsRevision, planning.Settings.Revision);
        Assert.Equal(envelope.ResolutionRevision, planning.Resolution.ResolutionRevision);
        Assert.Equal(envelope.RuntimeRevision, planning.Runtime.RuntimeRevision);
        Assert.Equal(envelope.ProviderRevision, planning.Runtime.ProviderRevision);
        Assert.Equal(envelope.PermissionRevision, planning.Runtime.PermissionRevision);
        Assert.Equal(envelope.McpRevision, planning.Runtime.McpRevision);
        Assert.Equal(envelope.ReconcilerRevision, planning.Runtime.ReconcilerRevision);
    }

    private static void AssertSelectionsEqual(
        IReadOnlyDictionary<string, bool> expected,
        IReadOnlyDictionary<string, bool> actual) =>
        Assert.Equal(
            expected.OrderBy(item => item.Key, StringComparer.Ordinal),
            actual.OrderBy(item => item.Key, StringComparer.Ordinal));

    private sealed record OwnerFixture(
        CapabilitySettingsSnapshotOwner Owner,
        ControlledPersistence Persistence,
        IReadOnlyList<CapabilityDescriptor> Descriptors);

    private sealed class ControlledPersistence : ICapabilityAvailabilitySettingsPersistence
    {
        private readonly object _gate = new();
        private CapabilityAvailabilitySettings _currentSettings;
        private CapabilityAvailabilityLoadResult? _loadOverride;
        private int _saveCallCount;

        public ControlledPersistence(CapabilityAvailabilitySettings initialSettings)
        {
            _currentSettings = initialSettings;
        }

        public Func<string, CapabilityAvailabilitySettings, CapabilityAvailabilitySaveResult>? SaveOverride { get; set; }

        public int SaveCallCount => Volatile.Read(ref _saveCallCount);

        public string? LastExpectedRevision { get; private set; }

        public CapabilityAvailabilitySettings? LastRequestedSettings { get; private set; }

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
            LastExpectedRevision = expectedRevision;
            LastRequestedSettings = settings;
            var saveOverride = SaveOverride;
            return saveOverride is null
                ? CompareExchangeSave(expectedRevision, settings)
                : saveOverride(expectedRevision, settings);
        }

        public CapabilityAvailabilitySaveResult CompareExchangeSave(
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

        public void PublishExternal(CapabilityAvailabilitySettings settings)
        {
            lock (_gate)
            {
                _currentSettings = settings;
                _loadOverride = null;
            }
        }

        public void SetLoadResult(CapabilityAvailabilityLoadResult loadResult)
        {
            lock (_gate)
            {
                _loadOverride = loadResult;
            }
        }
    }
}
