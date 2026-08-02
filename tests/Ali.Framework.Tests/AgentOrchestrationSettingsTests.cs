using Ali.Modules.Coordinator;

namespace Ali.Framework.Tests;

public sealed class AgentOrchestrationSettingsTests
{
    [Theory]
    [InlineData(MagenticPolicies.Off)]
    [InlineData(MagenticPolicies.AskFirst)]
    [InlineData(MagenticPolicies.Automatic)]
    [InlineData("retired-policy")]
    public void LegacyOrchestrationSelections_AreInertAndOmittedFromJson(string policy)
    {
        var root = Path.Combine(Path.GetTempPath(), "AliAgentOrchestrationSettingsTests", Guid.NewGuid().ToString("N"));
        try
        {
            AgentOrchestrationSettingsStore.Save(root, new AgentOrchestrationSettings
            {
                MagenticPolicy = policy,
                MagenticMaximumRounds = 7,
                ProgrammingAgentMode = ProgrammingAgentModes.Aider,
                AlwaysUseProgrammingAgent = true,
                OpenHandsWslDistribution = "Legacy-Distro"
            });

            var loaded = AgentOrchestrationSettingsStore.LoadOrDefault(root);
            var persisted = File.ReadAllText(AgentOrchestrationSettingsStore.GetPath(root));

            Assert.Equal(MagenticPolicies.Off, loaded.MagenticPolicy);
            Assert.Equal(6, loaded.MagenticMaximumRounds);
            Assert.Equal(ProgrammingAgentModes.Off, loaded.ProgrammingAgentMode);
            Assert.False(loaded.AlwaysUseProgrammingAgent);
            Assert.DoesNotContain("magenticPolicy", persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("magenticMaximumRounds", persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("programmingAgentMode", persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("alwaysUseProgrammingAgent", persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("openHandsWslDistribution", persisted, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void LegacySerializedOrchestrationSelection_IsIgnoredAndScrubbedOnNextSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliAgentOrchestrationLegacyTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                AgentOrchestrationSettingsStore.GetPath(root),
                """
                {
                  "magenticPolicy": "automatic-complex",
                  "magenticMaximumRounds": 9,
                  "programmingAgentMode": "aider",
                  "alwaysUseProgrammingAgent": true,
                  "openHandsWslDistribution": "Legacy-Distro"
                }
                """);

            var loaded = AgentOrchestrationSettingsStore.LoadOrDefault(root);

            Assert.Equal(MagenticPolicies.Off, loaded.MagenticPolicy);
            Assert.Equal(6, loaded.MagenticMaximumRounds);
            Assert.Equal(ProgrammingAgentModes.Off, loaded.ProgrammingAgentMode);
            Assert.False(loaded.AlwaysUseProgrammingAgent);

            AgentOrchestrationSettingsStore.Save(root, loaded);
            var rewritten = File.ReadAllText(AgentOrchestrationSettingsStore.GetPath(root));
            Assert.DoesNotContain("magenticPolicy", rewritten, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("magenticMaximumRounds", rewritten, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("programmingAgentMode", rewritten, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("alwaysUseProgrammingAgent", rewritten, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("openHandsWslDistribution", rewritten, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(MagenticPolicies.Off)]
    [InlineData(MagenticPolicies.AskFirst)]
    [InlineData(MagenticPolicies.Automatic)]
    [InlineData(" future-policy ")]
    public void MagenticPolicy_NormalizesEveryLegacyValueToOff(string? policy)
    {
        Assert.Equal([MagenticPolicies.Off], MagenticPolicies.All);
        Assert.Equal(MagenticPolicies.Off, MagenticPolicies.Normalize(policy));
    }

    [Fact]
    public void SavingSettings_DoesNotMoveOrDeleteLegacyCheckpointData()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "AliAgentOrchestrationCheckpointPreservationTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var checkpointPath = AgentOrchestrationSettingsStore.GetCheckpointPath(root);
            Directory.CreateDirectory(checkpointPath);
            var markerPath = Path.Combine(checkpointPath, "legacy-checkpoint.json");
            const string marker = "legacy checkpoint data stays untouched";
            File.WriteAllText(markerPath, marker);

            AgentOrchestrationSettingsStore.Save(root, new AgentOrchestrationSettings
            {
                MagenticPolicy = MagenticPolicies.Automatic,
                MagenticMaximumRounds = 12
            });

            Assert.True(File.Exists(markerPath));
            Assert.Equal(marker, File.ReadAllText(markerPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void AgentsTab_ShowsOneLoopAndLiveBridgeWithoutLegacyOrchestrationControls()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("src", "UI", "SettingsWindow.xaml"));

        Assert.Contains("SettingsAgentOrchestrationTab", xaml, StringComparison.Ordinal);
        Assert.Contains("One orchestration loop", xaml, StringComparison.Ordinal);
        Assert.Contains("Installed Agent Skills", xaml, StringComparison.Ordinal);
        Assert.Contains("SettingsConversationBridgeEnabled", xaml, StringComparison.Ordinal);
        Assert.Contains("SettingsConversationBridgeApprovalControl", xaml, StringComparison.Ordinal);
        Assert.Contains("SettingsSaveConversationBridge", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsMagenticPolicy", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsMagenticMaximumRounds", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Magentic", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Durable workflow checkpoints", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CheckpointSummary", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckpointPath", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ArchiveCheckpointsCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsSaveAgentOrchestration", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsAgentOrchestrationStatus", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsProgrammingAgentMode", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsAlwaysUseProgrammingAgent", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Programming engines", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsOpenHandsWslDistribution", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsRefreshProgrammingAgents", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenHands", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Aider", xaml, StringComparison.Ordinal);

        var viewModel = File.ReadAllText(FindRepositoryFile(
            "src", "UI", "ViewModels", "AgentOrchestrationSettingsViewModel.cs"));
        Assert.Contains("one Agent Framework execution loop", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Magentic", viewModel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MaximumRoundChoices", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckpointPath", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckpointSummary", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("ArchiveCheckpointsCommand", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Move", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyExternalSelection_NormalizesToAliAndRemovesExternalTools()
    {
        var settings = new AgentOrchestrationSettings
        {
            ProgrammingAgentMode = ProgrammingAgentModes.OpenHands,
            AlwaysUseProgrammingAgent = true
        }.Normalize();
        var instructions = AliToolCatalog.BuildInstructions(
            "Ali",
            settings);
        var inventory = AliCapabilityCatalog.ListAvailableTools(settings);

        Assert.Equal(ProgrammingAgentModes.Off, settings.ProgrammingAgentMode);
        Assert.False(settings.AlwaysUseProgrammingAgent);
        Assert.DoesNotContain(inventory.Tools, item => item.Name == AliCapabilityCatalog.CodingAgentStatusName);
        Assert.DoesNotContain(inventory.Tools, item => item.Name == AliCapabilityCatalog.CodingAgentExecuteName);
        Assert.Contains("Ali is the sole coding executor", instructions, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProgrammingAgentModes.Off)]
    [InlineData(ProgrammingAgentModes.Aider)]
    [InlineData(ProgrammingAgentModes.OpenHands)]
    [InlineData(ProgrammingAgentModes.Hybrid)]
    [InlineData("retired-provider")]
    [InlineData(" AIDER ")]
    public void ProgrammingAgentMode_NormalizesEveryLegacySelectionToOff(string mode)
    {
        var settings = new AgentOrchestrationSettings
        {
            ProgrammingAgentMode = mode,
            AlwaysUseProgrammingAgent = true
        }.Normalize();

        Assert.Equal(ProgrammingAgentModes.Off, settings.ProgrammingAgentMode);
        Assert.False(settings.AlwaysUseProgrammingAgent);
    }

    [Fact]
    public void OffProgrammingAgentMode_RemovesExternalAgentsAndClearsAlwaysUse()
    {
        var settings = new AgentOrchestrationSettings
        {
            ProgrammingAgentMode = ProgrammingAgentModes.Off,
            AlwaysUseProgrammingAgent = true
        }.Normalize();

        var inventory = AliCapabilityCatalog.ListAvailableTools(settings);

        Assert.False(settings.AlwaysUseProgrammingAgent);
        Assert.DoesNotContain(inventory.Tools, item => item.Name == AliCapabilityCatalog.CodingAgentStatusName);
        Assert.DoesNotContain(inventory.Tools, item => item.Name == AliCapabilityCatalog.CodingAgentExecuteName);
        Assert.Contains(
            "Ali is the sole coding executor",
            AliToolCatalog.BuildInstructions("Ali", settings),
            StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(segments)}");
    }
}
