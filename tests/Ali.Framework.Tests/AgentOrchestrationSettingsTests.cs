using Ali.Modules.Coordinator;

namespace Ali.Framework.Tests;

public sealed class AgentOrchestrationSettingsTests
{
    [Theory]
    [InlineData(MagenticPolicies.Off)]
    [InlineData(MagenticPolicies.AskFirst)]
    [InlineData(MagenticPolicies.Automatic)]
    public void Settings_RoundTripSupportedMagenticPolicies(string policy)
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
                OpenHandsWslDistribution = "Ubuntu-24.04"
            });

            var loaded = AgentOrchestrationSettingsStore.LoadOrDefault(root);

            Assert.Equal(policy, loaded.MagenticPolicy);
            Assert.Equal(7, loaded.MagenticMaximumRounds);
            Assert.Equal(ProgrammingAgentModes.Aider, loaded.ProgrammingAgentMode);
            Assert.True(loaded.AlwaysUseProgrammingAgent);
            Assert.Equal("Ubuntu-24.04", loaded.OpenHandsWslDistribution);
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
        Assert.Contains("Coding selector beside Effort", xaml, StringComparison.Ordinal);
        Assert.Contains("SettingsOpenHandsWslDistribution", xaml, StringComparison.Ordinal);
        Assert.Contains("SettingsRefreshProgrammingAgents", xaml, StringComparison.Ordinal);
        Assert.Contains("concurrent and background agents are disabled", xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AlwaysUseProgrammingAgent_RequiresModelSelectedCodingWorkToUseConfiguredEngine()
    {
        var instructions = AliToolCatalog.BuildInstructions(
            "Ali",
            new AgentOrchestrationSettings
            {
                ProgrammingAgentMode = ProgrammingAgentModes.OpenHands,
                AlwaysUseProgrammingAgent = true
            });

        Assert.Contains("call coding_agent_execute", instructions, StringComparison.Ordinal);
        Assert.Contains("semantically determine", instructions, StringComparison.Ordinal);
        Assert.Contains("selected mode is openhands", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("owns its architecture, file edits, terminal work, build/test cycle, diagnosis and repairs", instructions, StringComparison.Ordinal);
        Assert.Contains("do not edit, replace, move, create or delete project source yourself", instructions, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProgrammingAgentModes.Off)]
    [InlineData(ProgrammingAgentModes.Aider)]
    [InlineData(ProgrammingAgentModes.OpenHands)]
    public void ProgrammingAgentMode_NormalizesSupportedSelections(string mode)
    {
        Assert.Equal(mode, new AgentOrchestrationSettings { ProgrammingAgentMode = mode }.Normalize().ProgrammingAgentMode);
        Assert.Equal(ProgrammingAgentModes.Off,
            new AgentOrchestrationSettings { ProgrammingAgentMode = "retired-provider" }.Normalize().ProgrammingAgentMode);
    }

    [Fact]
    public void LegacyHybridSelection_FallsBackToAliInsteadOfRunningTwoAgents()
    {
        var settings = new AgentOrchestrationSettings
        {
            ProgrammingAgentMode = ProgrammingAgentModes.Hybrid,
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
        Assert.Contains("Aider and OpenHands are disabled", AliToolCatalog.BuildInstructions("Ali", settings));
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
