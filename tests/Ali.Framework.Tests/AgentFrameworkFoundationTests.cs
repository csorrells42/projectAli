namespace Ali.Framework.Tests;

public sealed class AgentFrameworkFoundationTests
{
    private static readonly string RepositoryRoot = TestRepository.Root;

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
    public void ProductionComposition_ConstructsNoRetiredSecondaryAgentGraph()
    {
        var runner = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "Modules", "Coordinator", "AliAgentHarnessRunner.cs"));
        var coding = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "Modules", "Coding", "AliCodingModule.cs"));
        var catalog = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "Modules", "Coordinator", "AliToolCatalog.cs"));
        var services = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "AliServices.cs"));
        var headless = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "Modules", "Mcp", "HeadlessMcpToolRuntime.cs"));

        Assert.DoesNotContain("new AliSpecialistAgentFactory", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("new AliAgentWorkflowFactory", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateStandardTools(", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateMagenticTool(", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("ProgressReported +=", catalog, StringComparison.Ordinal);
        Assert.Contains("var codingModule = new AliCodingModule(", services, StringComparison.Ordinal);
        Assert.Contains("durableOrchestrationRoot:", services, StringComparison.Ordinal);
        Assert.Contains("assistantProfileBinding:", services, StringComparison.Ordinal);
        Assert.Contains("new AliCodingModule(fileAccess)", headless, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthoritativeInventoryIncludesFrameworkToolsAndRetiresNestedOrchestration()
    {
        var inventory = Ali.Modules.Coordinator.AliCapabilityCatalog.ListAvailableTools(
            new Ali.Modules.Coordinator.AgentOrchestrationSettings());
        var names = inventory.Tools.Select(tool => tool.Name).ToArray();

        Assert.Equal(119, names.Length);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        var retiredNames = new[]
        {
            Ali.Modules.Coordinator.AliCapabilityCatalog.ConsultSoftwareEngineerName,
            Ali.Modules.Coordinator.AliCapabilityCatalog.ConsultResearcherName,
            Ali.Modules.Coordinator.AliCapabilityCatalog.ConsultOfficeSpecialistName,
            Ali.Modules.Coordinator.AliCapabilityCatalog.RunResearchArtifactWorkflowName,
            Ali.Modules.Coordinator.AliCapabilityCatalog.RunProgrammingGroupChatName,
            Ali.Modules.Coordinator.AliCapabilityCatalog.RunMagenticOrchestrationName,
            Ali.Modules.Coordinator.AliCapabilityCatalog.ListRecoverableWorkflowsName,
            Ali.Modules.Coordinator.AliCapabilityCatalog.ResumeWorkflowCheckpointName
        };
        Assert.All(retiredNames, retiredName => Assert.DoesNotContain(retiredName, names));
        Assert.Contains(Ali.Modules.Coordinator.AliCapabilityCatalog.CreateGoogleMapsDirectionsLinkName, names);
        Assert.Contains(Ali.Modules.Coordinator.AliCapabilityCatalog.GetActiveUserProfileName, names);
        Assert.DoesNotContain(Ali.Modules.Coordinator.AliCapabilityCatalog.RememberCurrentUserName, names);
        Assert.DoesNotContain(Ali.Modules.Coordinator.AliCapabilityCatalog.CorrectCurrentUserMemoryName, names);
        Assert.Contains(Ali.Modules.Coordinator.AliCapabilityCatalog.GetAgentModeName, names);
        Assert.Contains(Ali.Modules.Coordinator.AliCapabilityCatalog.SetAgentModeName, names);
        Assert.Contains(Ali.Modules.Coordinator.AliCapabilityCatalog.LoadAgentSkillName, names);
        Assert.Contains(Ali.Modules.Coordinator.AliCapabilityCatalog.ReadAgentSkillResourceName, names);
        Assert.Contains(Ali.Modules.Coordinator.AliCapabilityCatalog.RunAgentSkillScriptName, names);
    }

    [Fact]
    public void ArchitectureDocumentsOneAuthoritativePlannerAndInertLegacyCheckpoints()
    {
        var path = Path.Combine(RepositoryRoot, "docs", "AgentFrameworkArchitecture.md");
        var text = File.ReadAllText(path);
        Assert.Contains("one production Agent Framework Harness agent", text, StringComparison.Ordinal);
        Assert.Contains("does not construct or register private specialist agents", text, StringComparison.Ordinal);
        Assert.Contains("Legacy nested-workflow checkpoint files", text, StringComparison.Ordinal);
        Assert.Contains("does not open, modify, delete, offer, or resume them", text, StringComparison.Ordinal);
        Assert.Contains("Activity reports the visible lifecycle", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MinimumMessage_NeverFabricatesAUserMessageOrMasksTheRuntimeFinishReason()
    {
        var path = Path.Combine(
            RepositoryRoot,
            "src",
            "Modules",
            "Coordinator",
            "AliMinimumMessage.cs");
        var text = File.ReadAllText(path);

        Assert.DoesNotContain("ChatRole.User", text, StringComparison.Ordinal);
        Assert.DoesNotContain("blocker.ContinuationInstruction", text, StringComparison.Ordinal);
        Assert.Contains("nextInput = Array.Empty<ChatMessage>();", text, StringComparison.Ordinal);
        Assert.Contains("while (completedToolResults < MaximumToolResults)", text, StringComparison.Ordinal);
        Assert.Contains("finishReason ??= ChatFinishReason.Stop.ToString();", text, StringComparison.Ordinal);
        Assert.DoesNotContain("finishReason = ChatFinishReason.Stop.ToString();", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CoreCoding_RemovesSerenaOnboardingButKeepsCodingTools()
    {
        var onboarding = Microsoft.Extensions.AI.AIFunctionFactory.Create(
            () => "setup",
            "onboarding",
            "Create Serena project memories.");
        var editFile = Microsoft.Extensions.AI.AIFunctionFactory.Create(
            (string relative_path) => relative_path,
            "replace_symbol_body",
            "Edit a source symbol.");

        var filtered = Ali.Modules.Coordinator.AliAgentHarnessRunner
            .FilterSerenaToolsForCoreCoding([onboarding, editFile]);

        var retained = Assert.Single(filtered);
        Assert.Equal("replace_symbol_body", Assert.IsAssignableFrom<Microsoft.Extensions.AI.AIFunctionDeclaration>(retained).Name);
    }
}
