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
                MagenticMaximumRounds = 7
            });

            var loaded = AgentOrchestrationSettingsStore.LoadOrDefault(root);

            Assert.Equal(policy, loaded.MagenticPolicy);
            Assert.Equal(7, loaded.MagenticMaximumRounds);
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
        Assert.Contains("Magentic activation policy is automatic-complex", instructions);
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
        Assert.Contains("concurrent and background agents are disabled", xaml, StringComparison.OrdinalIgnoreCase);
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
