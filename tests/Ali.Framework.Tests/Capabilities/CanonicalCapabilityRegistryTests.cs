using Ali.Modules.Capabilities;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.Capabilities;

public sealed class CanonicalCapabilityRegistryTests
{
    [Fact]
    public void CanonicalGroups_HaveStableExactDefaultEnabledSet()
    {
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
            CanonicalCapabilityCatalog.Groups.Select(group => group.Id));
        Assert.Equal(CapabilityGroupIds.All, CanonicalCapabilityCatalog.Groups.Select(group => group.Id));
        Assert.All(CanonicalCapabilityCatalog.Groups, group => Assert.True(group.EnabledByDefault));

    }

    [Fact]
    public void CanonicalPresets_HaveStableExactMembershipAndOrder()
    {
        var expected = new (string Id, string[] GroupIds)[]
        {
            ("csharp",
            [
                "files-and-archives",
                "programming-core",
                "csharp-dotnet-roslyn",
                "devops-architecture-quality"
            ]),
            ("java",
            [
                "files-and-archives",
                "programming-core",
                "java",
                "devops-architecture-quality"
            ]),
            ("arduino",
            [
                "files-and-archives",
                "programming-core",
                "native-cpp-gcc",
                "arduino",
                "devops-architecture-quality"
            ]),
            ("file-tools", ["files-and-archives"])
        };

        Assert.Equal(expected.Select(item => item.Id), CanonicalCapabilityCatalog.Presets.Select(preset => preset.Id));
        foreach (var (id, groupIds) in expected)
        {
            Assert.Equal(groupIds, CanonicalCapabilityCatalog.GetPreset(id).GroupIds);
        }
    }

    [Fact]
    public void Presets_AreAdditiveAndArbitraryPresetObjectsAreRejected()
    {
        var settings = CapabilityAvailabilitySettings.CreateDefault()
            .WithGroupSelection(CapabilityGroupIds.CSharpDotNetRoslyn, false)
            .WithGroupSelection(CapabilityGroupIds.DevOpsArchitectureQuality, false)
            .WithGroupSelection(CapabilityGroupIds.Python, false)
            .WithGroupSelection("future-hidden-group", true)
            .ApplyPreset(CapabilityPresetIds.CSharp);

        Assert.True(settings.IsEnabled(CapabilityGroupIds.CSharpDotNetRoslyn));
        Assert.True(settings.IsEnabled(CapabilityGroupIds.FilesAndArchives));
        Assert.True(settings.IsEnabled(CapabilityGroupIds.ProgrammingCore));
        Assert.True(settings.IsEnabled(CapabilityGroupIds.DevOpsArchitectureQuality));
        Assert.True(settings.IsEnabled("future-hidden-group"));
        Assert.False(settings.IsEnabled(CapabilityGroupIds.Python));

        var injected = new CapabilityPresetDescriptor(
            CapabilityPresetIds.FileTools,
            "File Tools",
            "Enable file, folder, metadata, hashing, and archive tooling.",
            new[] { CapabilityGroupIds.FilesAndArchives, CapabilityGroupIds.Python });
        Assert.Throws<ArgumentException>(() => settings.ApplyPreset(injected));
    }

    [Fact]
    public void RegistryAndFrozenRevision_DeepCopyEveryCallerOwnedCollection()
    {
        var supportedProviderIds = new List<string> { "dotnet-roslyn" };
        var prerequisiteGroupIds = new List<string> { CapabilityGroupIds.ProgrammingCore };
        var presetIds = new List<string> { CapabilityPresetIds.CSharp };
        var semanticMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["intent"] = "inspect syntax"
        };
        var descriptor = new CapabilityDescriptor(
            "dotnet.inspect",
            "dotnet_roslyn_inspect",
            "Inspect C#",
            "Inspect a C# target with Roslyn.",
            CapabilityTier.Task,
            CapabilityGroupIds.CSharpDotNetRoslyn,
            "dotnet-roslyn",
            CapabilityRegistrationKind.LanguageProvider,
            "dotnet.inspect.schema.v1",
            () => AIFunctionFactory.Create(
                () => "ok",
                "dotnet_roslyn_inspect",
                "Inspect a C# target with Roslyn."),
            string.Empty,
            new CapabilityProviderGate(
                CapabilityProviderGateKind.AnySupported,
                supportedProviderIds),
            prerequisiteGroupIds,
            presetIds,
            new CapabilityPermissionDescriptor("source-read", false, CapabilityRiskLevel.Low),
            "inspect csharp dotnet roslyn syntax project solution",
            semanticMetadata,
            true,
            true,
            new CapabilityMcpExposure(true, "dotnet_roslyn_inspect"),
            ReadEffect());
        var registry = Registry(
            [descriptor],
            [new CapabilityProviderBinding("dotnet-roslyn", CapabilityGroupIds.CSharpDotNetRoslyn)]);
        var originalRegistryRevision = registry.RegistryRevision;
        var settings = CapabilityAvailabilitySettings.CreateDefault();
        var frozen = registry.Freeze(settings);

        supportedProviderIds.Add("changed-after-construction");
        prerequisiteGroupIds.Clear();
        presetIds.Clear();
        semanticMetadata["intent"] = "changed outside registry";
        settings = settings.WithGroupSelection(CapabilityGroupIds.CSharpDotNetRoslyn, false);

        Assert.Equal(originalRegistryRevision, registry.RegistryRevision);
        Assert.Equal(
            new[] { "dotnet-roslyn" },
            frozen.Descriptors[0].ProviderGate.SupportedProviderIds);
        Assert.Equal(new[] { CapabilityGroupIds.ProgrammingCore }, frozen.Descriptors[0].PrerequisiteGroupIds);
        Assert.Equal(new[] { CapabilityPresetIds.CSharp }, frozen.Descriptors[0].PresetIds);
        Assert.Equal("inspect syntax", frozen.Descriptors[0].SemanticMetadata["intent"]);
        Assert.Contains(frozen.Presets, preset => preset.Id == CapabilityPresetIds.CSharp);
        Assert.True(frozen.GroupSelections[CapabilityGroupIds.CSharpDotNetRoslyn]);
        Assert.NotEqual(settings.Revision, frozen.SettingsRevision);
        Assert.Equal(64, registry.RegistryRevision.Length);
    }

    [Fact]
    public void RegistryRevision_IsOrderIndependentAndIncludesCompleteMetadata()
    {
        var first = Descriptor("first", "first_tool", CapabilityGroupIds.FilesAndArchives);
        var second = Descriptor("second", "second_tool", CapabilityGroupIds.ProgrammingCore);
        var forward = Registry([first, second]);
        var reverse = Registry([second, first]);
        var changed = Registry(
        [
            first with { SemanticSearchText = first.SemanticSearchText + " changed" },
            second
        ]);

        Assert.Equal(forward.RegistryRevision, reverse.RegistryRevision);
        Assert.NotEqual(forward.RegistryRevision, changed.RegistryRevision);
        Assert.Equal(new[] { "first", "second" }, forward.Descriptors.Select(item => item.Id));
    }

    [Fact]
    public void RegistryRevision_TracksValidSecurityAndAvailabilityMetadataAndIgnoresSetOrder()
    {
        var providerBindings = new[]
        {
            new CapabilityProviderBinding("dotnet-roslyn", CapabilityGroupIds.CSharpDotNetRoslyn),
            new CapabilityProviderBinding("python-cpython", CapabilityGroupIds.Python)
        };
        var baseline = Descriptor(
            "coding.shared",
            "coding_shared",
            CapabilityGroupIds.ProgrammingCore,
            providerGate: new CapabilityProviderGate(
                CapabilityProviderGateKind.AnySupported,
                ["dotnet-roslyn", "python-cpython"]),
            prerequisiteGroupIds: [CapabilityGroupIds.CSharpDotNetRoslyn, CapabilityGroupIds.Python]) with
        {
            SemanticMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["intent"] = "shared coding",
                ["scope"] = "local"
            }
        };
        var reordered = baseline with
        {
            ProviderGate = baseline.ProviderGate with
            {
                SupportedProviderIds = baseline.ProviderGate.SupportedProviderIds.Reverse().ToArray()
            },
            PrerequisiteGroupIds = baseline.PrerequisiteGroupIds.Reverse().ToArray(),
            PresetIds = baseline.PresetIds.Reverse().ToArray(),
            SemanticMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["scope"] = "local",
                ["intent"] = "shared coding"
            }
        };
        var expectedRevision = Registry([baseline], providerBindings).RegistryRevision;

        Assert.Equal(
            expectedRevision,
            Registry([reordered], providerBindings.Reverse().ToArray()).RegistryRevision);

        var mutations = new (string Name, CapabilityDescriptor Descriptor)[]
        {
            ("provider gate kind", baseline with
            {
                ProviderGate = baseline.ProviderGate with { Kind = CapabilityProviderGateKind.ResolvedTarget }
            }),
            ("supported provider set", baseline with
            {
                ProviderGate = baseline.ProviderGate with { SupportedProviderIds = ["dotnet-roslyn"] }
            }),
            ("prerequisite set", baseline with
            {
                PrerequisiteGroupIds = [CapabilityGroupIds.CSharpDotNetRoslyn]
            }),
            ("permission policy", baseline with
            {
                Permission = baseline.Permission with { PolicyId = "alternate-policy" }
            }),
            ("approval requirement", baseline with
            {
                Permission = baseline.Permission with { RequiresApproval = true }
            }),
            ("risk level", baseline with
            {
                Permission = baseline.Permission with { Risk = CapabilityRiskLevel.Medium }
            }),
            ("MCP exposure", baseline with
            {
                McpExposure = new CapabilityMcpExposure(true, "coding_shared")
            }),
            ("effect metadata", baseline with
            {
                Effect = new CapabilityEffectDescriptor(
                    CapabilityEffectKind.LocalMutation,
                    "Writes local test state.",
                    CapabilityMutationBoundary.PermissionGuarded,
                    true,
                    "filesystem",
                    true,
                    true,
                    false,
                    false,
                    false)
            }),
            ("semantic metadata", baseline with
            {
                SemanticMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["intent"] = "shared coding",
                    ["scope"] = "workspace"
                }
            })
        };

        foreach (var (name, descriptor) in mutations)
        {
            var actualRevision = Registry([descriptor], providerBindings).RegistryRevision;
            Assert.False(
                string.Equals(expectedRevision, actualRevision, StringComparison.Ordinal),
                $"Valid mutation '{name}' must change the registry revision.");
        }
    }

    [Fact]
    public void Registry_RejectsInvalidPrerequisiteEffectAndDuplicateMcpName()
    {
        var unknownPrerequisite = Descriptor(
            "bad-prerequisite",
            "bad_prerequisite",
            CapabilityGroupIds.CSharpDotNetRoslyn,
            prerequisiteGroupIds: ["not-a-group"]);
        Assert.Throws<ArgumentException>(
            () => Registry([unknownPrerequisite]));

        var invalidMutation = Descriptor(
            "bad-mutation",
            "bad_mutation",
            CapabilityGroupIds.FilesAndArchives,
            effect: new CapabilityEffectDescriptor(
                CapabilityEffectKind.SourceMutation,
                "Mutates source through an unrelated external journal boundary.",
                CapabilityMutationBoundary.JournaledExternal,
                false,
                "filesystem",
                true,
                true,
                false,
                false,
                false));
        Assert.Throws<ArgumentException>(() => Registry([invalidMutation]));

        var first = Descriptor(
            "mcp-first",
            "mcp_first",
            CapabilityGroupIds.FilesAndArchives,
            mcpExposure: new CapabilityMcpExposure(true, "shared_name"));
        var second = Descriptor(
            "mcp-second",
            "mcp_second",
            CapabilityGroupIds.FilesAndArchives,
            mcpExposure: new CapabilityMcpExposure(true, "shared_name"));
        Assert.Throws<ArgumentException>(() => Registry([first, second]));
    }

    [Fact]
    public void Registry_RejectsDynamicProtocolToolsMissingProviderBindingsAndPresetDrift()
    {
        var dynamicProtocol = Descriptor(
            "incoming.protocol",
            "mcp_protocol",
            groupId: null,
            tier: CapabilityTier.Protocol,
            registrationKind: CapabilityRegistrationKind.Mcp,
            providerId: "mcp:server");
        Assert.Throws<ArgumentException>(() => Registry([dynamicProtocol]));

        var unreservedNativeProtocol = Descriptor(
            "native.protocol-bypass",
            "ordinary_native_tool",
            groupId: null,
            tier: CapabilityTier.Protocol);
        Assert.Throws<ArgumentException>(() => Registry([unreservedNativeProtocol]));

        var unboundProvider = Descriptor(
            "coding.shared",
            "coding_shared",
            CapabilityGroupIds.ProgrammingCore,
            providerGate: new CapabilityProviderGate(
                CapabilityProviderGateKind.AnySupported,
                ["python-cpython"]));
        Assert.Throws<ArgumentException>(() => Registry([unboundProvider]));

        var presetDrift = Descriptor(
            "files.read",
            "files_read",
            CapabilityGroupIds.FilesAndArchives) with
        {
            PresetIds = Array.Empty<string>()
        };
        Assert.Throws<ArgumentException>(() => Registry([presetDrift]));
    }

    [Fact]
    public void Registry_MaterializesAndFingerprintsTheExactDeclaredSchemaOnce()
    {
        var descriptor = Descriptor(
            "files.read",
            "file_access_read",
            CapabilityGroupIds.FilesAndArchives);
        var registry = Registry([descriptor]);
        var frozen = Assert.Single(registry.Descriptors);

        Assert.Equal(64, frozen.SchemaFingerprint.Length);
        var function = Assert.IsAssignableFrom<AIFunctionDeclaration>(frozen.SchemaFactory());
        Assert.Equal(frozen.ToolName, function.Name);
        Assert.Equal(frozen.SchemaFingerprint, CapabilitySchemaIdentity.Calculate(function));

        var wrongName = descriptor with
        {
            SchemaFactory = () => AIFunctionFactory.Create(
                () => "wrong",
                "different_tool",
                "Different tool.")
        };
        Assert.Throws<ArgumentException>(() => Registry([wrongName]));
    }

    [Fact]
    public void RegistryRevision_IncludesProviderBindingsAndRejectsContradictoryExternalEffects()
    {
        var descriptor = Descriptor(
            "coding.shared",
            "coding_shared",
            CapabilityGroupIds.ProgrammingCore,
            providerGate: new CapabilityProviderGate(
                CapabilityProviderGateKind.AnySupported,
                ["shared-provider"]));
        var first = Registry(
            [descriptor],
            [new CapabilityProviderBinding("shared-provider", CapabilityGroupIds.Python)]);
        var changed = Registry(
            [descriptor],
            [new CapabilityProviderBinding("shared-provider", CapabilityGroupIds.Java)]);
        Assert.NotEqual(first.RegistryRevision, changed.RegistryRevision);

        var contradictoryExternalRead = Descriptor(
            "external.read",
            "external_read",
            CapabilityGroupIds.FilesAndArchives,
            effect: new CapabilityEffectDescriptor(
                CapabilityEffectKind.ExternalRead,
                "Reads an external service.",
                CapabilityMutationBoundary.None,
                true,
                null,
                false,
                false,
                false,
                false,
                false));
        Assert.Throws<ArgumentException>(() => Registry([contradictoryExternalRead]));
    }

    internal static CapabilityDescriptor Descriptor(
        string id,
        string toolName,
        string? groupId,
        CapabilityTier tier = CapabilityTier.Task,
        CapabilityRegistrationKind registrationKind = CapabilityRegistrationKind.Native,
        string providerId = "test-provider",
        CapabilityProviderGate? providerGate = null,
        IReadOnlyList<string>? prerequisiteGroupIds = null,
        CapabilityMcpExposure? mcpExposure = null,
        CapabilityEffectDescriptor? effect = null) =>
        CapabilityDescriptor.Create(
            id,
            toolName,
            id,
            $"Description for {id}.",
            tier,
            groupId,
            providerId,
            registrationKind,
            $"{id}.schema.v1",
            () => AIFunctionFactory.Create(() => "ok", toolName, $"Description for {id}."),
            providerGate: providerGate
                ?? new CapabilityProviderGate(
                    CapabilityProviderGateKind.OwnerOnly,
                    Array.Empty<string>()),
            prerequisiteGroupIds: prerequisiteGroupIds ?? Array.Empty<string>(),
            presetIds: PresetsForGroup(groupId),
            permission: new CapabilityPermissionDescriptor(
                "test-permission",
                false,
                CapabilityRiskLevel.Low),
            semanticSearchText: $"semantic search text for {id}",
            semanticMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["intent"] = "test metadata"
            },
            visibleInCapabilityReport: true,
            visibleInCriticInventory: true,
            mcpExposure: mcpExposure ?? new CapabilityMcpExposure(false, null),
            effect: effect ?? ReadEffect());

    internal static CanonicalCapabilityRegistry Registry(
        IReadOnlyList<CapabilityDescriptor> descriptors,
        IReadOnlyList<CapabilityProviderBinding>? providerBindings = null) =>
        new(descriptors, providerBindings ?? Array.Empty<CapabilityProviderBinding>());

    internal static IReadOnlyList<string> PresetsForGroup(string? groupId) =>
        groupId is null
            ? Array.Empty<string>()
            : CanonicalCapabilityCatalog.Presets
                .Where(preset => preset.GroupIds.Contains(groupId, StringComparer.Ordinal))
                .Select(preset => preset.Id)
                .ToArray();

    internal static CapabilityEffectDescriptor ReadEffect() =>
        new(
            CapabilityEffectKind.Read,
            "Reads test state.",
            CapabilityMutationBoundary.None,
            true,
            null,
            true,
            false,
            false,
            false,
            false);
}
