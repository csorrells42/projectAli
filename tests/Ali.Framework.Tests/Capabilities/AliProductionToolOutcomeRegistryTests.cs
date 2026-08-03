using System.Text.Json;
using Ali.Modules.Coding.SourceControl;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Planning;

namespace Ali.Framework.Tests.Capabilities;

public sealed class AliProductionToolOutcomeRegistryTests
{
    [Fact]
    public void Contracts_CoverTheExact119ToolProductionCatalog()
    {
        Assert.Equal(119, AliProductionToolOutcomeRegistry.ContractedToolNames.Count);
        Assert.True(AliProductionCapabilityCatalog.KnownToolNames.SetEquals(
            AliProductionToolOutcomeRegistry.ContractedToolNames));
        Assert.Equal(
            100,
            AliProductionToolOutcomeRegistry.ContractedToolNames.Count(toolName =>
                AliProductionToolOutcomeRegistry.GetContractKind(toolName)
                == AliToolOutcomeContractKind.TypedReturn));
        Assert.Equal(
            19,
            AliProductionToolOutcomeRegistry.ContractedToolNames.Count(toolName =>
                AliProductionToolOutcomeRegistry.GetContractKind(toolName)
                == AliToolOutcomeContractKind.ProviderBoundarySignal));
        Assert.All(AliProductionToolOutcomeRegistry.ContractedToolNames, toolName =>
        {
            Assert.True(AliProductionToolOutcomeRegistry.TryGetContractId(
                toolName,
                out var contractId));
            Assert.Equal($"ali-outcome.{toolName}.v1", contractId);
        });
    }

    [Fact]
    public void ExactGitContract_AllowsAnEmptyButSuccessfulDiff()
    {
        var registry = Registry();
        var result = new SourceControlResult(
            Success: true,
            Operation: "diff",
            RepositoryRoot: "Workspace/repo",
            Summary: "No changes.",
            Output: string.Empty,
            ExitCode: 0);

        var outcome = registry.Classify(Request(
            AliCapabilityCatalog.GitDiffName,
            result));

        Assert.Equal(PlanningToolDomainOutcome.Succeeded, outcome);
    }

    [Fact]
    public void ExactTypedFailure_RemainsFailed()
    {
        var registry = Registry();
        var result = new SourceControlResult(
            Success: false,
            Operation: "status",
            RepositoryRoot: "Workspace/repo",
            Summary: "Git status failed.",
            Output: string.Empty,
            ExitCode: 128);

        Assert.Equal(
            PlanningToolDomainOutcome.Failed,
            registry.Classify(Request(AliCapabilityCatalog.GitStatusName, result)));
    }

    [Fact]
    public void GenericSuccessJson_CannotSatisfyATypedContract()
    {
        using var document = JsonDocument.Parse("""{"success":true}""");
        var registry = Registry();

        var outcome = registry.Classify(Request(
            AliCapabilityCatalog.GitStatusName,
            document.RootElement.Clone()));

        Assert.Equal(PlanningToolDomainOutcome.Unreported, outcome);
    }

    [Fact]
    public void AResultOwnedByAnotherTool_CannotCrossTheNameBoundary()
    {
        var registry = Registry();
        var result = new SourceControlResult(
            Success: true,
            Operation: "status",
            RepositoryRoot: "Workspace/repo",
            Summary: "Clean.",
            Output: string.Empty,
            ExitCode: 0);

        var outcome = registry.Classify(Request(
            AliCapabilityCatalog.RoslynAnalyzeProjectName,
            result));

        Assert.Equal(PlanningToolDomainOutcome.Unreported, outcome);
    }

    [Fact]
    public void EmptyFrameworkList_SucceedsOnlyFromExactNoMatchesSignal()
    {
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var registry = new AliProductionToolOutcomeRegistry(sidecar);
        var request = Request(AliCapabilityCatalog.FileListName, result: Array.Empty<object>());
        sidecar.Record(
            new AliFrameworkToolOutcomeKey(
                request.TurnIdentity,
                request.CallId,
                request.ToolName),
            AliFrameworkToolOutcomeSignal.NoMatches);

        Assert.Equal(PlanningToolDomainOutcome.Succeeded, registry.Classify(request));
        Assert.Equal(PlanningToolDomainOutcome.Unreported, registry.Classify(request));
    }

    [Theory]
    [InlineData(
        (int)AliFrameworkToolOutcomeSignal.Rejected,
        (int)PlanningToolDomainOutcome.Failed)]
    [InlineData(
        (int)AliFrameworkToolOutcomeSignal.Failed,
        (int)PlanningToolDomainOutcome.Failed)]
    [InlineData(
        (int)AliFrameworkToolOutcomeSignal.Conflicted,
        (int)PlanningToolDomainOutcome.Unreported)]
    public void FrameworkNonSuccessSignals_NeverPromote(
        int signalValue,
        int expectedValue)
    {
        var signal = (AliFrameworkToolOutcomeSignal)signalValue;
        var expected = (PlanningToolDomainOutcome)expectedValue;
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var registry = new AliProductionToolOutcomeRegistry(sidecar);
        var request = Request(AliCapabilityCatalog.FileReadName, "ambiguous provider text");
        sidecar.Record(
            new AliFrameworkToolOutcomeKey(
                request.TurnIdentity,
                request.CallId,
                request.ToolName),
            signal);

        Assert.Equal(expected, registry.Classify(request));
    }

    [Fact]
    public void MissingFrameworkSignal_IsUnreportedEvenForANormalReturn()
    {
        var registry = Registry();

        var outcome = registry.Classify(Request(
            AliCapabilityCatalog.FileReadName,
            "ordinary content or an ordinary error string"));

        Assert.Equal(PlanningToolDomainOutcome.Unreported, outcome);
    }

    [Fact]
    public void EveryProviderBoundaryContract_IsFailClosedWithoutItsExactSignal()
    {
        var registry = Registry();
        var providerTools = AliProductionToolOutcomeRegistry.ContractedToolNames
            .Where(toolName =>
                AliProductionToolOutcomeRegistry.GetContractKind(toolName)
                == AliToolOutcomeContractKind.ProviderBoundarySignal)
            .ToArray();

        Assert.Equal(19, providerTools.Length);
        Assert.All(providerTools, toolName => Assert.Equal(
            PlanningToolDomainOutcome.Unreported,
            registry.Classify(Request(toolName, "ordinary provider return"))));
    }

    [Fact]
    public void ColdRegistry_DoesNotRecoverAProviderSignalFromAnotherInstance()
    {
        var previousProcessSidecar = new AliFrameworkToolOutcomeSidecar();
        var request = Request(AliCapabilityCatalog.FileReadName, "ordinary content");
        previousProcessSidecar.Record(
            new AliFrameworkToolOutcomeKey(
                request.TurnIdentity,
                request.CallId,
                request.ToolName),
            AliFrameworkToolOutcomeSignal.Found);

        var coldRegistry = new AliProductionToolOutcomeRegistry(
            new AliFrameworkToolOutcomeSidecar());

        Assert.Equal(
            PlanningToolDomainOutcome.Unreported,
            coldRegistry.Classify(request));
        Assert.Equal(1, previousProcessSidecar.Count);
    }

    [Fact]
    public void UnknownExternalTool_RemainsUnreportedAndConsumesNoGenericSuccessClaim()
    {
        using var document = JsonDocument.Parse("""{"success":true}""");
        var sidecar = new AliFrameworkToolOutcomeSidecar();
        var registry = new AliProductionToolOutcomeRegistry(sidecar);
        var request = Request("external_mcp_unknown", document.RootElement.Clone());
        sidecar.Record(
            new AliFrameworkToolOutcomeKey(
                request.TurnIdentity,
                request.CallId,
                request.ToolName),
            AliFrameworkToolOutcomeSignal.Completed);

        Assert.Equal(PlanningToolDomainOutcome.Unreported, registry.Classify(request));
        Assert.Equal(0, sidecar.Count);
    }

    private static AliProductionToolOutcomeRegistry Registry() =>
        new(new AliFrameworkToolOutcomeSidecar());

    private static AliCompletedToolOutcomeRequest Request(string toolName, object? result) =>
        new(
            new TurnIdentity("user", "conversation", "assistant"),
            "call-1",
            toolName,
            result);
}
