using System.Reflection;
using Ali.Modules.Capabilities;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.Capabilities;

public sealed class CapabilitySettingsProjectionTests
{
    [Fact]
    public void ProviderRows_ReportControlledGenericToolsWithoutDoubleCountingEnvelopeTotals()
    {
        var generic = CanonicalCapabilityRegistryTests.Descriptor(
            "coding.build",
            "coding_build_project",
            CapabilityGroupIds.ProgrammingCore,
            providerId: "ali-core",
            providerGate: new CapabilityProviderGate(
                CapabilityProviderGateKind.ResolvedTarget,
                ["dotnet-roslyn", "python-cpython"]));
        var registry = CanonicalCapabilityRegistryTests.Registry(
            [generic],
            [
                new CapabilityProviderBinding("dotnet-roslyn", CapabilityGroupIds.CSharpDotNetRoslyn),
                new CapabilityProviderBinding("python-cpython", CapabilityGroupIds.Python)
            ]);
        var runtime = new CapabilityRuntimeAvailability(
            "user-a",
            "runtime-provider-rows",
            [CapabilityRuntimeToolRegistration.Create(generic.SchemaFactory(), generic.SchemaFactoryId)],
            "providers-1",
            ["ali-core", "dotnet-roslyn", "python-cpython"],
            targetResolution: null,
            "permissions-1",
            [generic.Permission.PolicyId],
            "mcp-1",
            [],
            [],
            "reconcilers-1",
            []);
        var settings = CapabilityAvailabilitySettings.CreateDefault();
        var frozen = registry.Freeze(settings);
        var resolution = new CapabilityResolver().ResolvePlanning(frozen, runtime);
        var enabled = CapabilitySettingsProjection.Create(
            frozen,
            resolution,
            "provider-row-publication-1",
            CapabilityAvailabilityLoadStatus.Loaded,
            loadError: null);

        var python = Assert.Single(enabled.Rows, row => row.GroupId == CapabilityGroupIds.Python);
        Assert.Equal(CapabilitySettingsRowStatus.Ready, python.Status);
        Assert.Equal(1, python.DeclaredToolCount);
        Assert.Equal(1, python.CallableToolCount);
        Assert.Equal(1, enabled.DeclaredTaskToolCount);
        Assert.Equal(1, enabled.CallableTaskToolCount);

        var disabledSettings = settings.WithGroupSelection(CapabilityGroupIds.Python, false);
        var disabledFrozen = registry.Freeze(disabledSettings);
        var disabledResolution = new CapabilityResolver().ResolvePlanning(disabledFrozen, runtime);
        var disabled = CapabilitySettingsProjection.Create(
            disabledFrozen,
            disabledResolution,
            "provider-row-publication-2",
            CapabilityAvailabilityLoadStatus.Loaded,
            loadError: null);
        var disabledPython = Assert.Single(
            disabled.Rows,
            row => row.GroupId == CapabilityGroupIds.Python);

        Assert.Equal(CapabilitySettingsRowStatus.Disabled, disabledPython.Status);
        Assert.Equal(1, disabledPython.DeclaredToolCount);
        Assert.Equal(0, disabledPython.CallableToolCount);
        Assert.Equal(1, disabledPython.UnavailableToolCount);
    }

    [Fact]
    public void Projection_UsesCanonicalRowsPresetsAndOneResolutionForExactStatusCountsAndReasons()
    {
        var envelope = CreateProjection();

        Assert.Equal(
            new[]
            {
                "capability-discovery",
                "personal-context-and-memory",
                "web-research-and-navigation",
                "reminders-and-calendar",
                "work-memory",
                "agent-modes-and-skills",
                "specialists-and-workflows",
                "files-and-archives",
                "programming-core",
                "csharp-dotnet-roslyn",
                "python",
                "web-html-css-js-ts",
                "java",
                "native-cpp-gcc",
                "arduino",
                "raspberry-pi",
                "devops-architecture-quality",
                "visual-studio"
            },
            envelope.Rows.Select(row => row.GroupId));
        Assert.Equal(18, envelope.KnownGroupCount);
        Assert.Equal(
            CanonicalCapabilityCatalog.Groups.Select(group => group.DisplayName),
            envelope.Rows.Select(row => row.Capability));
        Assert.Equal(
            CanonicalCapabilityCatalog.Groups.Select(group => group.Description),
            envelope.Rows.Select(row => row.Description));

        Assert.Equal(
            new[] { "csharp", "java", "arduino", "file-tools" },
            envelope.Presets.Select(preset => preset.Id));
        Assert.Equal(4, envelope.Presets.Count);
        foreach (var projectedPreset in envelope.Presets)
        {
            var canonicalPreset = CanonicalCapabilityCatalog.GetPreset(projectedPreset.Id);
            Assert.Equal(canonicalPreset.DisplayName, projectedPreset.DisplayName);
            Assert.Equal(canonicalPreset.Description, projectedPreset.Description);
            Assert.Equal(canonicalPreset.GroupIds, projectedPreset.GroupIds);
            Assert.Equal(1, projectedPreset.WouldEnableGroupCount);
            Assert.False(projectedPreset.IsFullyApplied);
        }

        var rows = envelope.Rows.ToDictionary(row => row.GroupId, StringComparer.Ordinal);
        AssertRow(
            rows[CapabilityGroupIds.FilesAndArchives],
            enabled: false,
            CapabilitySettingsRowStatus.Disabled,
            declared: 1,
            callable: 0,
            unavailable: 1);
        AssertRow(
            rows[CapabilityGroupIds.ProgrammingCore],
            enabled: true,
            CapabilitySettingsRowStatus.Empty,
            declared: 0,
            callable: 0,
            unavailable: 0);
        AssertRow(
            rows[CapabilityGroupIds.CSharpDotNetRoslyn],
            enabled: true,
            CapabilitySettingsRowStatus.Ready,
            declared: 2,
            callable: 2,
            unavailable: 0);
        AssertRow(
            rows[CapabilityGroupIds.Python],
            enabled: true,
            CapabilitySettingsRowStatus.Degraded,
            declared: 2,
            callable: 1,
            unavailable: 1);
        AssertRow(
            rows[CapabilityGroupIds.WebHtmlCssJavaScriptTypeScript],
            enabled: true,
            CapabilitySettingsRowStatus.Unavailable,
            declared: 1,
            callable: 0,
            unavailable: 1);
        Assert.True(
            Enum.GetValues<CapabilitySettingsRowStatus>()
                .ToHashSet()
                .SetEquals(envelope.Rows.Select(row => row.Status)));

        AssertReason(
            Assert.Single(rows[CapabilityGroupIds.FilesAndArchives].Reasons),
            "files.read",
            "files_read",
            CapabilityAvailabilityReasonCode.GroupDisabled,
            CapabilityGroupIds.FilesAndArchives,
            "Capability group 'files-and-archives' is disabled.");
        Assert.Empty(rows[CapabilityGroupIds.CSharpDotNetRoslyn].Reasons);
        AssertReason(
            Assert.Single(rows[CapabilityGroupIds.Python].Reasons),
            "python.missing",
            "python_missing",
            CapabilityAvailabilityReasonCode.RuntimeToolMissing,
            "python_missing",
            "Runtime tool 'python_missing' is not registered.");
        AssertReason(
            Assert.Single(rows[CapabilityGroupIds.WebHtmlCssJavaScriptTypeScript].Reasons),
            "web.inspect",
            "web_inspect",
            CapabilityAvailabilityReasonCode.ProviderUnavailable,
            "web-provider",
            "Provider 'web-provider' is unavailable.");

        Assert.Equal(17, envelope.EnabledGroupCount);
        Assert.Equal(1, envelope.DisabledGroupCount);
        Assert.Equal(6, envelope.DeclaredTaskToolCount);
        Assert.Equal(3, envelope.CallableTaskToolCount);
        Assert.Equal(3, envelope.UnavailableTaskToolCount);
        Assert.Equal(1, envelope.CallableProtocolToolCount);
        Assert.Equal(0, envelope.UnavailableProtocolToolCount);
        Assert.Equal(0, envelope.QuarantinedRuntimeToolCount);
        Assert.Equal(envelope.DeclaredTaskToolCount, envelope.Rows.Sum(row => row.DeclaredToolCount));
        Assert.Equal(envelope.CallableTaskToolCount, envelope.Rows.Sum(row => row.CallableToolCount));
        Assert.DoesNotContain(
            envelope.Rows.SelectMany(row => row.Reasons),
            reason => reason.CapabilityId == "protocol.submit");

        Assert.Equal(2, envelope.UnknownSelectionCount);
        Assert.Equal(
            new[] { ("alpha-unknown", false), ("zeta-unknown", true) },
            envelope.UnknownSelections.Select(selection => (selection.GroupId, selection.Enabled)));
        Assert.Equal(CapabilityAvailabilityLoadStatus.Loaded, envelope.LoadStatus);
        Assert.Null(envelope.LoadError);
        Assert.Equal("user-a", envelope.ActiveUserId);
        Assert.Equal("runtime-1", envelope.RuntimeRevision);
        Assert.Equal(64, envelope.RegistryRevision.Length);
        Assert.Equal(64, envelope.SettingsRevision.Length);
        Assert.Equal(64, envelope.ResolutionRevision.Length);
    }

    [Fact]
    public void DtoConstructors_CopyEveryNestedCollectionAndExposeOnlyReadOnlyViews()
    {
        var reasonSource = new List<CapabilitySettingsReason>
        {
            new(
                "capability-a",
                "tool_a",
                CapabilityAvailabilityReasonCode.RuntimeToolMissing,
                "tool_a",
                "Tool A is unavailable.")
        };
        var row = new CapabilitySettingsRow(
            "group-a",
            "Group A",
            "Description A",
            true,
            CapabilitySettingsRowStatus.Degraded,
            2,
            1,
            1,
            reasonSource);
        var presetGroupSource = new List<string> { "group-a" };
        var preset = new CapabilitySettingsPreset(
            "preset-a",
            "Preset A",
            "Description P",
            presetGroupSource,
            1);
        var unknown = new CapabilitySettingsUnknownSelection("future-group", true);
        var rowSource = new List<CapabilitySettingsRow> { row };
        var presetSource = new List<CapabilitySettingsPreset> { preset };
        var unknownSource = new List<CapabilitySettingsUnknownSelection> { unknown };
        var envelope = new CapabilitySettingsEnvelope(
            new CapabilitySettingsStamp("publication", "registry", "settings", "resolution"),
            "user",
            "runtime",
            "providers",
            "permissions",
            "mcp",
            "reconcilers",
            CapabilityAvailabilityLoadStatus.Loaded,
            null,
            1,
            1,
            0,
            2,
            1,
            1,
            0,
            0,
            0,
            unknownSource,
            rowSource,
            presetSource);

        reasonSource.Clear();
        presetGroupSource.Clear();
        rowSource.Clear();
        presetSource.Clear();
        unknownSource.Clear();

        Assert.Same(row, Assert.Single(envelope.Rows));
        Assert.Same(preset, Assert.Single(envelope.Presets));
        Assert.Same(unknown, Assert.Single(envelope.UnknownSelections));
        Assert.Single(row.Reasons);
        Assert.Equal(new[] { "group-a" }, preset.GroupIds);
        AssertReadOnly(envelope.Rows);
        AssertReadOnly(envelope.Presets);
        AssertReadOnly(envelope.UnknownSelections);
        AssertReadOnly(row.Reasons);
        AssertReadOnly(preset.GroupIds);

        foreach (var dtoType in WalkPublicDtoGraph(typeof(CapabilitySettingsEnvelope))
                     .Where(IsAliApplicationType))
        {
            Assert.True(dtoType.IsSealed || dtoType.IsValueType);
            Assert.DoesNotContain(
                dtoType.GetProperties(BindingFlags.Instance | BindingFlags.Public),
                property => property.SetMethod?.IsPublic == true);
        }
    }

    [Fact]
    public void PublicEnvelopeDtoGraph_DoesNotExposeExecutionBearingTypes()
    {
        var graph = WalkPublicDtoGraph(typeof(CapabilitySettingsEnvelope));
        var publicDtoProperties = graph
            .Where(IsAliApplicationType)
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            .ToArray();

        Assert.Contains(typeof(CapabilitySettingsStamp), graph);
        Assert.Contains(typeof(CapabilitySettingsRow), graph);
        Assert.Contains(typeof(CapabilitySettingsReason), graph);
        Assert.Contains(typeof(CapabilitySettingsPreset), graph);
        Assert.Contains(typeof(CapabilitySettingsUnknownSelection), graph);
        Assert.DoesNotContain(typeof(CapabilityDescriptor), graph);
        Assert.DoesNotContain(typeof(CapabilitySchemaFactory), graph);
        Assert.DoesNotContain(graph, type => typeof(Delegate).IsAssignableFrom(type));
        Assert.DoesNotContain(graph, type => typeof(AITool).IsAssignableFrom(type));
        Assert.DoesNotContain(graph, type => typeof(AIFunctionDeclaration).IsAssignableFrom(type));
        Assert.DoesNotContain(
            graph,
            type => type.Namespace?.StartsWith("Microsoft.Extensions.AI", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(
            publicDtoProperties,
            property => property.Name.Contains("SchemaFactory", StringComparison.Ordinal));
    }

    private static CapabilitySettingsEnvelope CreateProjection()
    {
        var files = Descriptor("files.read", "files_read", CapabilityGroupIds.FilesAndArchives);
        var csharpInspect = Descriptor(
            "csharp.inspect",
            "csharp_inspect",
            CapabilityGroupIds.CSharpDotNetRoslyn);
        var csharpBuild = Descriptor(
            "csharp.build",
            "csharp_build",
            CapabilityGroupIds.CSharpDotNetRoslyn);
        var pythonReady = Descriptor("python.ready", "python_ready", CapabilityGroupIds.Python);
        var pythonMissing = Descriptor("python.missing", "python_missing", CapabilityGroupIds.Python);
        var webUnavailable = Descriptor(
            "web.inspect",
            "web_inspect",
            CapabilityGroupIds.WebHtmlCssJavaScriptTypeScript,
            providerId: "web-provider");
        var protocol = Descriptor(
            "protocol.submit",
            "submit_orchestration_decision",
            groupId: null,
            tier: CapabilityTier.Protocol);
        var descriptors = new[]
        {
            files,
            csharpInspect,
            csharpBuild,
            pythonReady,
            pythonMissing,
            webUnavailable,
            protocol
        };
        var registered = new[]
        {
            files,
            csharpInspect,
            csharpBuild,
            pythonReady,
            webUnavailable,
            protocol
        };
        var settingsSource = CapabilityAvailabilitySettings.CreateDefault()
            .GroupSelections
            .ToDictionary(selection => selection.Key, selection => selection.Value, StringComparer.Ordinal);
        settingsSource[CapabilityGroupIds.FilesAndArchives] = false;
        settingsSource["zeta-unknown"] = true;
        settingsSource["alpha-unknown"] = false;
        var settings = new CapabilityAvailabilitySettings(settingsSource);
        var registry = CanonicalCapabilityRegistryTests.Registry(descriptors);
        var frozen = registry.Freeze(settings);
        var runtime = new CapabilityRuntimeAvailability(
            "user-a",
            "runtime-1",
            registered.Select(descriptor => CapabilityRuntimeToolRegistration.Create(
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
        var resolution = new CapabilityResolver().ResolvePlanning(frozen, runtime);

        return CapabilitySettingsProjection.Create(
            frozen,
            resolution,
            "publication-1",
            CapabilityAvailabilityLoadStatus.Loaded,
            loadError: null);
    }

    private static CapabilityDescriptor Descriptor(
        string id,
        string toolName,
        string? groupId,
        string providerId = "test-provider",
        CapabilityTier tier = CapabilityTier.Task) =>
        CanonicalCapabilityRegistryTests.Descriptor(
            id,
            toolName,
            groupId,
            tier: tier,
            providerId: providerId);

    private static void AssertRow(
        CapabilitySettingsRow row,
        bool enabled,
        CapabilitySettingsRowStatus status,
        int declared,
        int callable,
        int unavailable)
    {
        Assert.Equal(enabled, row.Enabled);
        Assert.Equal(status, row.Status);
        Assert.Equal(declared, row.DeclaredToolCount);
        Assert.Equal(callable, row.CallableToolCount);
        Assert.Equal(unavailable, row.UnavailableToolCount);
    }

    private static void AssertReason(
        CapabilitySettingsReason reason,
        string capabilityId,
        string toolName,
        CapabilityAvailabilityReasonCode code,
        string dependencyId,
        string message)
    {
        Assert.Equal(capabilityId, reason.CapabilityId);
        Assert.Equal(toolName, reason.ToolName);
        Assert.Equal(code, reason.Code);
        Assert.Equal(dependencyId, reason.DependencyId);
        Assert.Equal(message, reason.Message);
    }

    private static void AssertReadOnly<T>(IReadOnlyList<T> values)
    {
        var list = Assert.IsAssignableFrom<IList<T>>(values);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(list.Clear);
    }

    private static IReadOnlySet<Type> WalkPublicDtoGraph(Type root)
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
        type.Assembly == typeof(CapabilitySettingsEnvelope).Assembly;
}
