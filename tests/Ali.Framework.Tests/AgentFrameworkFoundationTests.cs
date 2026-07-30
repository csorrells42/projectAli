namespace Ali.Framework.Tests;

public sealed class AgentFrameworkFoundationTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void ReviewedSkillsAreShippedWithProgressiveDisclosureMetadata()
    {
        var skills = Directory.GetFiles(Path.Combine(RepositoryRoot, "skills"), "SKILL.md", SearchOption.AllDirectories);
        Assert.Equal(4, skills.Length);
        Assert.All(skills, path =>
        {
            var text = File.ReadAllText(path);
            Assert.StartsWith("---", text);
            Assert.Contains("name:", text, StringComparison.Ordinal);
            Assert.Contains("description:", text, StringComparison.Ordinal);
            Assert.DoesNotContain("scripts/", text, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(skills, path => path.Contains("engineering-shop-floor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HarnessEnablesSkillsTelemetryAndSharedLifecycleMiddleware()
    {
        var runner = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "Modules", "Coordinator", "AliAgentHarnessRunner.cs"));
        Assert.Contains("DisableAgentSkillsProvider = false", runner, StringComparison.Ordinal);
        Assert.Contains("AgentFileSkillsSource", runner, StringComparison.Ordinal);
        Assert.Contains("ProjectAli.AgentFramework", runner, StringComparison.Ordinal);
        Assert.Contains("AliAgentFrameworkMiddleware.WithVisibleLifecycle", runner, StringComparison.Ordinal);
        Assert.Contains("DisableTodoProvider = true", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthoritativeInventoryIncludesFrameworkModeAndSkillTools()
    {
        var inventory = Ali.Modules.Coordinator.AliCapabilityCatalog.ListAvailableTools(
            new Ali.Modules.Coordinator.AgentOrchestrationSettings());
        var names = inventory.Tools.Select(tool => tool.Name).ToArray();

        Assert.Equal(120, names.Length);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(Ali.Modules.Coordinator.AliCapabilityCatalog.CreateGoogleMapsDirectionsLinkName, names);
        Assert.Contains(Ali.Modules.Coordinator.AliCapabilityCatalog.GetActiveUserProfileName, names);
        Assert.DoesNotContain(Ali.Modules.Coordinator.AliCapabilityCatalog.RememberCurrentUserName, names);
        Assert.DoesNotContain(Ali.Modules.Coordinator.AliCapabilityCatalog.CorrectCurrentUserMemoryName, names);
        Assert.Contains(Ali.Modules.Coordinator.AliCapabilityCatalog.GetAgentModeName, names);
        Assert.Contains(Ali.Modules.Coordinator.AliCapabilityCatalog.SetAgentModeName, names);
        Assert.Contains(Ali.Modules.Coordinator.AliCapabilityCatalog.LoadAgentSkillName, names);
        Assert.Contains(Ali.Modules.Coordinator.AliCapabilityCatalog.ReadAgentSkillResourceName, names);
        Assert.Contains(Ali.Modules.Coordinator.AliCapabilityCatalog.RunAgentSkillScriptName, names);
        Assert.Contains(Ali.Modules.Coordinator.AliCapabilityCatalog.ListRecoverableWorkflowsName, names);
        Assert.Contains(Ali.Modules.Coordinator.AliCapabilityCatalog.ResumeWorkflowCheckpointName, names);
    }

    [Fact]
    public void ArchitectureKeepsOnePersonalityAndBoundsMagenticMode()
    {
        var path = Path.Combine(RepositoryRoot, "docs", "AgentFrameworkArchitecture.md");
        var text = File.ReadAllText(path);
        Assert.Contains("Ali is the only user-facing identity", text, StringComparison.Ordinal);
        Assert.Contains("Concurrent/background agents:** disabled", text, StringComparison.Ordinal);
        Assert.Contains("Magentic is never used for greetings", text, StringComparison.Ordinal);
        Assert.Contains("Hidden reasoning is never", text, StringComparison.Ordinal);
    }
}
