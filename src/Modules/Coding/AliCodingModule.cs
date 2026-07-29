using Ali.Modules.Coordinator;
using Ali.Modules.Coding.Debugging;
using Ali.Modules.Coding.Engineering;
using Ali.Modules.Coding.Dependencies;
using Ali.Modules.Coding.SourceControl;
using Ali.Modules.Coding.Architecture;
using Ali.Modules.Coding.Quality;
using Ali.Modules.Coding.Performance;
using Ali.Modules.Coding.Verification;
using Ali.Modules.Coding.Release;
using Ali.Modules.Coding.Delivery;
using Ali.Modules.Coding.Indexing;
using Ali.Modules.Coding.Languages;
using Ali.Modules.Coding.Python;
using Ali.Modules.Coding.Web;
using Ali.Modules.Coding.Java;
using Ali.Modules.Coding.Cpp;
using Ali.Modules.WorkstationFiles;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Coding;

/// <summary>
/// Composition root for Ali's C# engineering capability. The coordinator and MCP
/// server share this one module instance and the same bounded tool definitions.
/// </summary>
public sealed class AliCodingModule : IAsyncDisposable
{
    internal AliCodingModule(AliWorkstationFileAccess fileAccess)
    {
        ArgumentNullException.ThrowIfNull(fileAccess);
        var auditPath = Path.Combine(Path.GetDirectoryName(fileAccess.Audit.Path)!, "dotnet-actions.jsonl");
        var tracker = new AliCodingProjectTracker();
        var resolver = new AliCodingProjectResolver(fileAccess);
        ProjectScaffolder = new AliDotNetProjectScaffolder(fileAccess, tracker, auditPath);
        Tools = new AliRoslynCodingTools(resolver, tracker, auditPath);
        EngineeringLoop = new AliDotNetEngineeringLoop(resolver);
        Debugger = new AliDotNetDebugger(resolver);
        Dependencies = new AliDependencyEngineering(resolver);
        SourceControl = new AliSourceControlEngineering(resolver);
        var workspaceLoader = new AliRoslynWorkspaceLoader(resolver);
        Architecture = new AliArchitectureEngineering(workspaceLoader);
        Quality = new AliQualityEngineering(resolver, Tools);
        Performance = new AliPerformanceEngineering(resolver);
        Verification = new AliApplicationVerification(resolver);
        Release = new AliReleaseEngineering(resolver, Architecture);
        Delivery = new AliAutonomousDelivery(Architecture, Quality, EngineeringLoop, Tools, Verification, Release);

        LanguageProviders = new AliLanguageProviderRegistry();
        var languageResolver = new AliLanguageProjectResolver(fileAccess);
        var toolchainLocator = new AliToolchainLocator();
        LanguageProviders.Register(new AliDotNetLanguageProvider(Tools, EngineeringLoop, toolchainLocator));
        LanguageProviders.Register(new AliPythonLanguageProvider(toolchainLocator));
        LanguageProviders.Register(new AliWebLanguageProvider(toolchainLocator));
        LanguageProviders.Register(new AliJavaLanguageProvider(toolchainLocator));
        LanguageProviders.Register(new AliCppLanguageProvider(toolchainLocator));
        var sourceIndex = new AliSourceIndexService(languageResolver);
        MultiLanguage = new AliMultiLanguageCodingTools(
            languageResolver,
            LanguageProviders,
            sourceIndex);
        CrossLanguageArchitecture = new AliCrossLanguageArchitecture(languageResolver, sourceIndex);
    }

    internal AliDotNetProjectScaffolder ProjectScaffolder { get; }
    internal AliRoslynCodingTools Tools { get; }
    internal AliDotNetEngineeringLoop EngineeringLoop { get; }
    internal AliDotNetDebugger Debugger { get; }
    internal AliDependencyEngineering Dependencies { get; }
    internal AliSourceControlEngineering SourceControl { get; }
    internal AliArchitectureEngineering Architecture { get; }
    internal AliQualityEngineering Quality { get; }
    internal AliPerformanceEngineering Performance { get; }
    internal AliApplicationVerification Verification { get; }
    internal AliReleaseEngineering Release { get; }
    internal AliAutonomousDelivery Delivery { get; }
    internal AliLanguageProviderRegistry LanguageProviders { get; }
    internal AliMultiLanguageCodingTools MultiLanguage { get; }
    internal AliCrossLanguageArchitecture CrossLanguageArchitecture { get; }

    internal IReadOnlyList<AIFunction> CreateFunctions() =>
    [
        AIFunctionFactory.Create(
            (Func<AliLanguageCapabilityReport>)MultiLanguage.GetCapabilities,
            AliCapabilityCatalog.CodingListCapabilitiesName,
            "Return Ali's authoritative live coding-provider, protocol, toolchain, execution, debugging, indexing, and architecture capabilities. Use this instead of guessing what Ali cannot do."),
        AIFunctionFactory.Create(
            (Func<string, AliCodingProjectInspection>)MultiLanguage.InspectProject,
            AliCapabilityCatalog.CodingInspectProjectName,
            "Detect the language and registered provider for an approved project manifest, solution, or source document."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<AliSourceIndexResult>>)MultiLanguage.IndexProjectAsync,
            AliCapabilityCatalog.CodingIndexProjectName,
            "Build a bounded local structural index for an approved C#, Python, web, Java, or C++ project."),
        AIFunctionFactory.Create(
            (Func<string, string, int?, CancellationToken, Task<AliSymbolSearchResult>>)SearchSymbolsAsync,
            AliCapabilityCatalog.CodingSearchSymbolsName,
            "Search Ali's local cross-language structural index. maximumResults defaults to 50 and cannot exceed 200."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<AliLanguageOperationResult>>)MultiLanguage.AnalyzeAsync,
            AliCapabilityCatalog.CodingAnalyzeProjectName,
            "Run the registered language provider's semantic and static analysis for an approved project."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<AliLanguageOperationResult>>)MultiLanguage.FormatAsync,
            AliCapabilityCatalog.CodingFormatProjectName,
            "Format an approved project through its registered language provider after user approval."),
        AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<AliLanguageOperationResult>>)MultiLanguage.BuildAsync,
            AliCapabilityCatalog.CodingBuildProjectName,
            "Build an approved project through its detected C#, Python, web, Java, or C++ provider."),
        AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<AliLanguageOperationResult>>)MultiLanguage.TestAsync,
            AliCapabilityCatalog.CodingTestProjectName,
            "Run an approved project's native test system through its registered language provider."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<AliCrossLanguageArchitectureReport>>)CrossLanguageArchitecture.InspectAsync,
            AliCapabilityCatalog.CodingInspectArchitectureName,
            "Build a bounded evidence-backed dependency graph, cycle list, hotspot ranking, and Mermaid view across C#, Python, web, Java, and C/C++ source."),
        AIFunctionFactory.Create(
            (Func<string, string, CancellationToken, Task<DotNetCreateProjectResult>>)ProjectScaffolder.CreateAsync,
            AliCapabilityCatalog.DotNetCreateProjectName,
            "Create a new C# project scaffold in an empty approved folder. projectPath must be a virtual .csproj path such as Desktop/TicTacToe/TicTacToe.csproj; template must be wpf or console. After success, write the complete requested source before building."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<RoslynAnalysisResult>>)Tools.AnalyzeAsync,
            AliCapabilityCatalog.RoslynAnalyzeProjectName,
            "Load an approved C# project with Roslyn and return semantic compiler diagnostics with exact source locations."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<RoslynFormatResult>>)Tools.FormatAsync,
            AliCapabilityCatalog.RoslynFormatProjectName,
            "Format every C# document in an approved project with Roslyn."),
        AIFunctionFactory.Create(
            (Func<string, string, CancellationToken, Task<RoslynSymbolResult>>)Tools.FindSymbolAsync,
            AliCapabilityCatalog.RoslynFindSymbolName,
            "Find C# type or member declarations semantically with Roslyn."),
        AIFunctionFactory.Create(
            (Func<string, string, int, int, CancellationToken, Task<RoslynCompletionResult>>)Tools.GetCompletionsAsync,
            AliCapabilityCatalog.RoslynGetCompletionsName,
            "Return Roslyn IntelliSense completion candidates at a one-based C# source position."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<RoslynSolutionOverviewResult>>)Tools.InspectSolutionAsync,
            AliCapabilityCatalog.RoslynInspectSolutionName,
            "Inspect an approved .csproj, .sln, or .slnx and return its C# project graph, references, target frameworks, and document counts."),
        AIFunctionFactory.Create(
            (Func<string, string, CancellationToken, Task<RoslynDocumentResult>>)Tools.InspectDocumentAsync,
            AliCapabilityCatalog.RoslynInspectDocumentName,
            "Return Roslyn's semantic outline, live diagnostics, and classified source spans for one C# document."),
        AIFunctionFactory.Create(
            (Func<string, string, int, int, CancellationToken, Task<RoslynPositionResult>>)Tools.InspectPositionAsync,
            AliCapabilityCatalog.RoslynInspectPositionName,
            "Return Roslyn hover text, definitions, and invocation signatures at a one-based C# source position."),
        AIFunctionFactory.Create(
            (Func<string, string, int, int, CancellationToken, Task<RoslynReferenceResult>>)Tools.FindReferencesAsync,
            AliCapabilityCatalog.RoslynFindReferencesName,
            "Find every semantic reference to the C# symbol at a one-based source position across a project or solution."),
        AIFunctionFactory.Create(
            (Func<string, string, int, int, string, CancellationToken, Task<RoslynRenameResult>>)Tools.PreviewRenameAsync,
            AliCapabilityCatalog.RoslynPreviewRenameName,
            "Preview Roslyn's exact solution-wide semantic rename without writing files."),
        AIFunctionFactory.Create(
            (Func<string, string, int, int, string, CancellationToken, Task<RoslynRenameResult>>)Tools.ApplyRenameAsync,
            AliCapabilityCatalog.RoslynApplyRenameName,
            "Apply Roslyn's solution-wide semantic rename after user approval."),
        AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<DotNetBuildResult>>)Tools.BuildAsync,
            AliCapabilityCatalog.DotNetBuildName,
            "Restore and compile an approved C# project through Microsoft's in-process MSBuild API."),
        AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<DotNetRunResult>>)Tools.RunAsync,
            AliCapabilityCatalog.DotNetRunName,
            "Launch an already-built .NET application from an approved project after user approval."),
        AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<DotNetTestResult>>)EngineeringLoop.TestAsync,
            AliCapabilityCatalog.DotNetTestName,
            "Discover and run tests for an approved .NET project or solution, returning structured failures and a stable TRX artifact."),
        AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<DotNetVerificationResult>>)VerifyAsync,
            AliCapabilityCatalog.DotNetVerifyName,
            "Run Ali's bounded build-and-test engineering loop and return structured evidence for the next repair decision."),
        AIFunctionFactory.Create(
            (Func<string, string?, bool, string?, int?, CancellationToken, Task<DotNetDebugSessionResult>>)Debugger.LaunchAsync,
            AliCapabilityCatalog.DotNetDebugLaunchName,
            "Launch an approved built .NET project under the CLR debugger, optionally stopping at entry or one source breakpoint."),
        AIFunctionFactory.Create(
            (Func<string, int, CancellationToken, Task<DotNetDebugSessionResult>>)Debugger.AttachAsync,
            AliCapabilityCatalog.DotNetDebugAttachName,
            "Attach the CLR debugger to a process running from an approved project folder."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<DotNetDebugSnapshot>>)Debugger.InspectAsync,
            AliCapabilityCatalog.DotNetDebugInspectName,
            "Inspect debugger threads, stack frames, locals, and the latest exception or breakpoint stop."),
        AIFunctionFactory.Create(
            (Func<string, string, int?, CancellationToken, Task<DotNetDebugEvaluation>>)Debugger.EvaluateAsync,
            AliCapabilityCatalog.DotNetDebugEvaluateName,
            "Evaluate a C# watch expression in an active CLR debugger frame."),
        AIFunctionFactory.Create(
            (Func<string, string, string, int[], CancellationToken, Task<IReadOnlyList<DebugBreakpoint>>>)Debugger.SetBreakpointsAsync,
            AliCapabilityCatalog.DotNetDebugBreakpointsName,
            "Replace source breakpoints for one approved project document in an active CLR debugger session."),
        AIFunctionFactory.Create(
            (Func<string, string, CancellationToken, Task<DotNetDebugControlResult>>)Debugger.ControlAsync,
            AliCapabilityCatalog.DotNetDebugControlName,
            "Continue, pause, step over, step into, or step out in an active CLR debugger session."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<DotNetDebugControlResult>>)Debugger.StopAsync,
            AliCapabilityCatalog.DotNetDebugStopName,
            "Terminate an active CLR debugger session and its debuggee."),
        AIFunctionFactory.Create(
            (Func<string, DebugDiagnosticsHandoff>)Debugger.GetDiagnosticsHandoff,
            AliCapabilityCatalog.DotNetDebugDiagnosticsHandoffName,
            "Return the bounded process handoff used by Ali's coverage and performance-diagnostics modules."),
        AIFunctionFactory.Create((Func<string, CancellationToken, Task<DependencyInspectionResult>>)Dependencies.InspectAsync,
            AliCapabilityCatalog.DotNetDependencyInspectName, "Inspect exact PackageReferences, lock-file state, and NuGet vulnerability/deprecation audit results."),
        AIFunctionFactory.Create((Func<string, string, string, string?, CancellationToken, Task<DependencyChangeResult>>)Dependencies.PreviewChangeAsync,
            AliCapabilityCatalog.DotNetDependencyPreviewName, "Preview an exact add, update, or remove PackageReference edit without writing the project."),
        AIFunctionFactory.Create((Func<string, string, string, string?, CancellationToken, Task<DependencyChangeResult>>)Dependencies.ApplyChangeAsync,
            AliCapabilityCatalog.DotNetDependencyApplyName, "Apply a previously considered exact PackageReference add, update, or remove after approval."),
        AIFunctionFactory.Create((Func<string, CancellationToken, Task<SourceControlResult>>)SourceControl.StatusAsync,
            AliCapabilityCatalog.GitStatusName, "Return authoritative Git branch and working-tree status for an approved coding target."),
        AIFunctionFactory.Create((Func<string, bool, CancellationToken, Task<SourceControlResult>>)SourceControl.DiffAsync,
            AliCapabilityCatalog.GitDiffName, "Return the current staged or unstaged Git patch for review."),
        AIFunctionFactory.Create((Func<string, int, CancellationToken, Task<SourceControlResult>>)SourceControl.HistoryAsync,
            AliCapabilityCatalog.GitHistoryName, "Return bounded Git commit history with hashes, timestamps, authors, and subjects."),
        AIFunctionFactory.Create((Func<string, string, CancellationToken, Task<SourceControlResult>>)SourceControl.BlameAsync,
            AliCapabilityCatalog.GitBlameName, "Return Git line history for one approved project document."),
        AIFunctionFactory.Create((Func<string, string, CancellationToken, Task<SourceControlResult>>)SourceControl.CreateBranchAsync,
            AliCapabilityCatalog.GitCreateBranchName, "Create and switch to a validated Git branch after approval."),
        AIFunctionFactory.Create((Func<string, string, CancellationToken, Task<SourceControlResult>>)SourceControl.CommitAsync,
            AliCapabilityCatalog.GitCommitName, "Commit already-staged changes with a bounded one-line message after approval."),
        AIFunctionFactory.Create((Func<string, string, string, CancellationToken, Task<SourceControlResult>>)SourceControl.PushAsync,
            AliCapabilityCatalog.GitPushName, "Push one validated branch to one validated remote after approval."),
        AIFunctionFactory.Create((Func<string, CancellationToken, Task<ArchitectureInspectionResult>>)Architecture.InspectAsync,
            AliCapabilityCatalog.ArchitectureInspectName, "Build semantic project and call graphs and report project cycles."),
        AIFunctionFactory.Create((Func<string, ArchitectureBoundaryRule[], CancellationToken, Task<ArchitectureBoundaryResult>>)Architecture.CheckBoundariesAsync,
            AliCapabilityCatalog.ArchitectureCheckName, "Evaluate explicit namespace dependency boundary rules against Roslyn semantic call edges."),
        AIFunctionFactory.Create((Func<string, CancellationToken, Task<QualityScanResult>>)Quality.ScanAsync,
            AliCapabilityCatalog.DotNetQualityScanName, "Run Roslyn diagnostics and bounded secret checks and write a stable SARIF quality artifact."),
        AIFunctionFactory.Create((Func<string, string?, int, CancellationToken, Task<PerformanceRunResult>>)Performance.MeasureAsync,
            AliCapabilityCatalog.DotNetPerformanceMeasureName, "Measure a built application over bounded iterations and write repeatable wall-time, CPU, and memory evidence."),
        AIFunctionFactory.Create((Func<string, string, string, CancellationToken, Task<PerformanceComparisonResult>>)Performance.CompareAsync,
            AliCapabilityCatalog.DotNetPerformanceCompareName, "Compare two saved performance evidence artifacts and report the measured regression or improvement."),
        AIFunctionFactory.Create((Func<string, int, int, CancellationToken, Task<PerformanceTraceResult>>)Performance.CaptureTraceAsync,
            AliCapabilityCatalog.DotNetPerformanceTraceName, "Capture a bounded managed EventPipe CPU/allocation trace from an approved project process."),
        AIFunctionFactory.Create((Func<string, string?, string?, CancellationToken, Task<ApplicationVerificationResult>>)Verification.SmokeTestAsync,
            AliCapabilityCatalog.DotNetApplicationVerifyName, "Run an actual console, desktop, service, or loopback-web smoke test and capture visible desktop evidence when applicable."),
        AIFunctionFactory.Create((Func<string, string?, bool, CancellationToken, Task<DotNetReleaseResult>>)Release.PublishAsync,
            AliCapabilityCatalog.DotNetReleasePublishName, "Create a bounded .NET publish folder and a cryptographic file manifest after approval."),
        AIFunctionFactory.Create((Func<string, CancellationToken, Task<EngineeringReportResult>>)Release.GenerateArchitectureReportAsync,
            AliCapabilityCatalog.DotNetArchitectureReportName, "Generate a source-backed Markdown architecture report from semantic project and call graphs."),
        AIFunctionFactory.Create((Func<string, string?, string?, bool, bool, CancellationToken, Task<AutonomousDeliveryResult>>)Delivery.VerifyDeliveryAsync,
            AliCapabilityCatalog.DotNetDeliveryVerifyName, "Run the final architecture, quality, build, test, optional app, and optional release evidence gates before claiming delivery complete. Supply testTargetPath when tests live in a separate project.")
    ];

    private Task<DotNetVerificationResult> VerifyAsync(string targetPath, string? configuration, CancellationToken cancellationToken) =>
        EngineeringLoop.VerifyAsync(targetPath, configuration, Tools.BuildAsync, cancellationToken);

    private Task<AliSymbolSearchResult> SearchSymbolsAsync(
        string targetPath,
        string query,
        int? maximumResults,
        CancellationToken cancellationToken) =>
        MultiLanguage.SearchSymbolsAsync(targetPath, query, maximumResults ?? 50, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in LanguageProviders.Providers)
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }
        await Debugger.DisposeAsync().ConfigureAwait(false);
    }
}
