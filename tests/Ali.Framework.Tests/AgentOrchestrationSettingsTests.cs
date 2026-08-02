using Ali.Modules.Coordinator;

namespace Ali.Framework.Tests;

public sealed class AgentOrchestrationSettingsTests
{
    [Theory]
    [InlineData(MagenticPolicies.Off)]
    [InlineData(MagenticPolicies.AskFirst)]
    [InlineData(MagenticPolicies.Automatic)]
    public void Settings_RoundTripMagenticPolicyWithoutRetiredProgrammingFields(string policy)
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

            Assert.Equal(policy, loaded.MagenticPolicy);
            Assert.Equal(7, loaded.MagenticMaximumRounds);
            Assert.Equal(ProgrammingAgentModes.Off, loaded.ProgrammingAgentMode);
            Assert.False(loaded.AlwaysUseProgrammingAgent);
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
    public void LegacySerializedProgrammingSelection_IsIgnoredAndScrubbedOnNextSave()
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

            Assert.Equal(MagenticPolicies.Automatic, loaded.MagenticPolicy);
            Assert.Equal(9, loaded.MagenticMaximumRounds);
            Assert.Equal(ProgrammingAgentModes.Off, loaded.ProgrammingAgentMode);
            Assert.False(loaded.AlwaysUseProgrammingAgent);

            AgentOrchestrationSettingsStore.Save(root, loaded);
            var rewritten = File.ReadAllText(AgentOrchestrationSettingsStore.GetPath(root));
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

    [Fact]
    public void OffPolicy_RemovesMagenticFromAuthoritativeInventoryAndPrompt()
    {
        var settings = new AgentOrchestrationSettings { MagenticPolicy = MagenticPolicies.Off };

        var inventory = AliCapabilityCatalog.ListAvailableTools(settings);
        var prompt = AliCapabilityCatalog.BuildPromptManifest(settings);

        Assert.DoesNotContain(inventory.Tools, item => item.Name == AliCapabilityCatalog.RunMagenticOrchestrationName);
        Assert.DoesNotContain(AliCapabilityCatalog.RunMagenticOrchestrationName, prompt, StringComparison.Ordinal);
        Assert.Contains("Magentic activation policy is off", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void MagenticBoundary_IsDocumentedAndBounded()
    {
        var instructions = AliToolCatalog.BuildInstructions(
            "Charlie",
            new AgentOrchestrationSettings
            {
                MagenticPolicy = MagenticPolicies.Automatic,
                MagenticMaximumRounds = 6
            });

        Assert.Contains(AliCapabilityCatalog.RunMagenticOrchestrationName, instructions);
        Assert.Contains("Use run_magentic_orchestration automatically only", instructions);
        Assert.Contains("Automatic for complex work", File.ReadAllText(FindArchitectureDocument()));
    }

    [Fact]
    public void SettingsTab_ExposesPolicyBoundAndCheckpointControls()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("src", "UI", "SettingsWindow.xaml"));

        Assert.Contains("SettingsAgentOrchestrationTab", xaml, StringComparison.Ordinal);
        Assert.Contains("SettingsMagenticPolicy", xaml, StringComparison.Ordinal);
        Assert.Contains("SettingsMagenticMaximumRounds", xaml, StringComparison.Ordinal);
        Assert.Contains("ArchiveCheckpointsCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsProgrammingAgentMode", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsAlwaysUseProgrammingAgent", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Programming engines", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsOpenHandsWslDistribution", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsRefreshProgrammingAgents", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenHands", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Aider", xaml, StringComparison.Ordinal);
        Assert.Contains("concurrent and background agents are disabled", xaml, StringComparison.OrdinalIgnoreCase);

        var viewModel = File.ReadAllText(FindRepositoryFile(
            "src", "UI", "ViewModels", "AgentOrchestrationSettingsViewModel.cs"));
        Assert.DoesNotContain("RefreshProgrammingAgents", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("ExternalAgents.GetStatusAsync", viewModel, StringComparison.Ordinal);
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

    private static string FindArchitectureDocument()
    {
        return FindRepositoryFile("docs", "AgentFrameworkArchitecture.md");
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
