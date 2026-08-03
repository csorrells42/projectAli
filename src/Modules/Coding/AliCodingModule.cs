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
using Ali.Modules.Coding.Changesets;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Coding.Operations;
using Ali.Modules.Coding.RoslynActions;
using Ali.Modules.Coding.RoslynQueries;
using Ali.Modules.Coding.Toolchains;
using Ali.Modules.Coding.VisualStudio;
using Ali.Modules.Coding.Native;
using Ali.Modules.Coding.Embedded.Arduino;
using Ali.Modules.Coding.Embedded.RaspberryPi;
using Ali.Modules.DevOpsExecution;
using Ali.Modules.Permissions;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Execution;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.Runtime;
using Ali.Modules.WorkstationFiles;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Coding;

internal sealed record AliCodingFunctionRegistration(AIFunction Function)
{
    public static implicit operator AliCodingFunctionRegistration(AIFunction function) =>
        new(function);
}

/// <summary>
/// Composition root for Ali's C# engineering capability. The coordinator and MCP
/// server share this one module instance and the same bounded tool definitions.
/// </summary>
public sealed class AliCodingModule : IAsyncDisposable
{
    internal AliCodingModule(
        AliWorkstationFileAccess fileAccess,
        Func<AgentOrchestrationSettings>? orchestrationSettings = null,
        Func<OpenAiCompatibleRuntimeOptions>? runtimeSettings = null,
        string? installRoot = null,
        string? durableOrchestrationRoot = null,
        string? assistantProfileBinding = null)
    {
        ArgumentNullException.ThrowIfNull(fileAccess);
        // Keep the legacy optional arguments source-compatible during the staged cutover,
        // but do not construct or register external coding-agent ownership in production.
        _ = orchestrationSettings;
        _ = runtimeSettings;
        _ = installRoot;
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
        var durableRoot = Path.GetFullPath(
            durableOrchestrationRoot
            ?? Path.Combine(Path.GetDirectoryName(auditPath)!, "OrchestrationV2"));
        var profileBinding = string.IsNullOrWhiteSpace(assistantProfileBinding)
            ? "ali-coding-module-v1"
            : assistantProfileBinding.Trim();
        var codingStateRoot = Path.Combine(durableRoot, "Coding");
        var evidence = new EvidenceLedger(durableRoot, profileBinding);
        var invocationStore = new AliDurableInvocationStore(
            Path.Combine(codingStateRoot, "Invocations"),
            profileBinding);
        var sourceChangeSets = new AliSourceChangeSetStore(
            Path.Combine(codingStateRoot, "Changesets"),
            profileBinding);
        var sourceValidator = new AliSourceChangeSetValidator(sourceChangeSets);
        var sourcePublisher = new AliSourceChangeSetPublisher(sourceChangeSets, sourceValidator);
        var sourceReconciler = new AliSourceChangeSetReconciler(sourceChangeSets, sourcePublisher);
        var referenceResolver = new AliRoslynTargetReferenceResolver();
        var solutionFingerprint = new AliRoslynSolutionFingerprint(referenceResolver);
        var semanticWorkspace = new AliRoslynSemanticWorkspaceBinding(
            workspaceLoader,
            solutionFingerprint);
        var diagnosticVerifier = new AliRoslynChangeSetVerifier();
        var previewWorkspaces = new AliRoslynPreviewWorkspaceManager(
            referenceResolver,
            solutionFingerprint);
        var roslynChangeSets = new AliRoslynChangeSetStore(
            sourceChangeSets,
            solutionFingerprint,
            diagnosticVerifier);
        var handles = new AliRoslynActionHandleStore(
            Path.Combine(codingStateRoot, "Handles"),
            profileBinding);
        var actionTargetStates = new AliRoslynActionTargetStateAdapter(
            resolver,
            handles,
            semanticWorkspace);
        RoslynProviderCatalog = AliRoslynOwnedProviderCatalog.CreateDefault();
        ActionDeck = new AliRoslynActionDeck(
            workspaceLoader,
            previewWorkspaces,
            RoslynProviderCatalog.CreateDiscovery(),
            roslynChangeSets,
            sourceChangeSets,
            handles,
            actionTargetStates,
            solutionFingerprint,
            diagnosticVerifier,
            new AliRoslynStagedBuildVerifier());
        ActionPublisher = new AliRoslynActionPublisher(
            handles,
            sourceChangeSets,
            sourcePublisher,
            workspaceLoader,
            solutionFingerprint);
        var actionPublicationRecovery = new AliRoslynActionPublicationRecovery(
            handles,
            sourceChangeSets,
            sourcePublisher,
            workspaceLoader,
            solutionFingerprint);
        var queryTargetStates = new AliRoslynQueryTargetStateAdapter(
            resolver,
            semanticWorkspace);
        Queries = new AliRoslynQueryFacade(
            Tools,
            resolver,
            workspaceLoader,
            queryTargetStates);
        var roslynExecutionEffectAdapters = AliRoslynActionExecutionAdapter.CreateAll(
                resolver,
                handles,
                sourceChangeSets,
                sourceReconciler,
                actionPublicationRecovery,
                actionTargetStates,
                evidence)
            .Concat(AliRoslynQueryExecutionAdapter.CreateAll(queryTargetStates))
            .ToArray();
        Architecture = new AliArchitectureEngineering(workspaceLoader);
        Quality = new AliQualityEngineering(resolver, Tools);
        Performance = new AliPerformanceEngineering(resolver);
        Verification = new AliApplicationVerification(resolver);
        Release = new AliReleaseEngineering(resolver, Architecture);
        Delivery = new AliAutonomousDelivery(Architecture, Quality, EngineeringLoop, Tools, Verification, Release);

        LanguageProviders = new AliLanguageProviderRegistry();
        var languageResolver = new AliLanguageProjectResolver(fileAccess);
        var toolchainLocator = new AliToolchainLocator();
        VisualStudio = new AliVisualStudioIntegration(languageResolver);
        GnuNative = new AliGnuNativeTools(languageResolver, toolchainLocator);
        Arduino = new AliArduinoTools(languageResolver, fileAccess);
        RaspberryPi = new AliRaspberryPiTools(languageResolver);
        LanguageProviders.Register(new AliDotNetLanguageProvider(Tools, EngineeringLoop, toolchainLocator));
        LanguageProviders.Register(new AliPythonLanguageProvider(toolchainLocator));
        LanguageProviders.Register(new AliWebLanguageProvider(toolchainLocator));
        LanguageProviders.Register(new AliJavaLanguageProvider(toolchainLocator));
        LanguageProviders.Register(new AliCppLanguageProvider(toolchainLocator));
        LanguageProviders.Register(new AliArduinoLanguageProvider(Arduino));
        var sourceIndex = new AliSourceIndexService(languageResolver);
        MultiLanguage = new AliMultiLanguageCodingTools(
            languageResolver,
            LanguageProviders,
            sourceIndex);
        CrossLanguageArchitecture = new AliCrossLanguageArchitecture(languageResolver, sourceIndex);
        Operations = new AliCodingOperations(languageResolver, LanguageProviders);
        var codingInvocationBindings = new AliCodingInvocationBindingResolver(
            fileAccess,
            resolver,
            languageResolver,
            LanguageProviders,
            Tools);
        CodingExecution = new AliCodingProcessExecutionCoordinator(
            codingInvocationBindings,
            invocationStore,
            evidence);
        GitExecution = new AliGitExecutionCoordinator(
            new AliGitInvocationBindingResolver(
                fileAccess,
                resolver,
                SourceControl.ProviderPin),
            invocationStore,
            evidence);
        DevOpsExecution = new AliDevOpsExecutionCoordinator(
            new AliDevOpsInvocationBindingResolver(resolver),
            invocationStore,
            evidence);
        TargetStateAdapters =
        [
            actionTargetStates,
            queryTargetStates,
            CodingExecution.TargetStates,
            GitExecution.TargetStates,
            DevOpsExecution.TargetStates
        ];
        ExecutionEffectAdapters = roslynExecutionEffectAdapters
            .Concat(CodingExecution.Adapters)
            .Concat(GitExecution.Adapters)
            .Concat(DevOpsExecution.Adapters)
            .ToArray();
    }

    internal AliDotNetProjectScaffolder ProjectScaffolder { get; }
    internal AliRoslynCodingTools Tools { get; }
    internal AliRoslynQueryFacade Queries { get; }
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
    internal AliCodingOperations Operations { get; }
    internal AliVisualStudioIntegration VisualStudio { get; }
    internal AliGnuNativeTools GnuNative { get; }
    internal AliArduinoTools Arduino { get; }
    internal AliRaspberryPiTools RaspberryPi { get; }
    internal AliRoslynActionDeck ActionDeck { get; }
    internal AliRoslynOwnedProviderCatalog RoslynProviderCatalog { get; }
    internal AliRoslynActionPublisher ActionPublisher { get; }
    internal AliCodingProcessExecutionCoordinator CodingExecution { get; }
    internal AliGitExecutionCoordinator GitExecution { get; }
    internal AliDevOpsExecutionCoordinator DevOpsExecution { get; }
    internal IReadOnlyList<IActionTargetStateAdapter> TargetStateAdapters { get; }
    internal IReadOnlyList<IAliExecutionEffectAdapter> ExecutionEffectAdapters { get; }
    internal IReadOnlyList<AIFunction> CreateFunctions() =>
        CreateFunctionRegistrations().Select(registration => registration.Function).ToArray();

    internal IReadOnlyList<AliCodingFunctionRegistration> CreateFunctionRegistrations() =>
    [
        AIFunctionFactory.Create((Func<AliVisualStudioReport>)VisualStudio.Inspect,
            AliCapabilityCatalog.VisualStudioInspectName,
            "Inspect installed Visual Studio instances and report MSBuild, MSVC, CMake, Ninja, test, formatting, language-server, and debugger features truthfully."),
        AIFunctionFactory.Create((Func<string, string?, string?, CancellationToken, Task<AliVisualStudioActionResult>>)VisualStudio.BuildAsync,
            AliCapabilityCatalog.VisualStudioBuildName,
            "Build an approved solution or project with the installed Visual Studio MSBuild engine and preserve a binary log."),
        AIFunctionFactory.Create((Func<string, string?, AliVisualStudioActionResult>)VisualStudio.Open,
            AliCapabilityCatalog.VisualStudioOpenName,
            "Open an approved solution, project, or document in the installed Visual Studio IDE."),
        AIFunctionFactory.Create((Func<AliGnuNativeToolchainReport>)GnuNative.Inspect,
            AliCapabilityCatalog.GnuNativeInspectName,
            "Inspect Ali's MSYS2 UCRT64 GCC, G++, GDB, Make, CMake, and Ninja native toolchain."),
        AIFunctionFactory.Create((Func<string, string, string?, CancellationToken, Task<AliLanguageOperationResult>>)GnuNative.ExecuteAsync,
            AliCapabilityCatalog.GnuNativeExecuteName,
            "Analyze, build, test, or run an approved C/C++ project with GCC UCRT64 instead of MSVC."),
        AIFunctionFactory.Create((Func<CancellationToken, Task<AliArduinoReport>>)Arduino.InspectAsync,
            AliCapabilityCatalog.ArduinoInspectName,
            "Inspect the installed Arduino CLI, IDE, board cores, connected boards, and embedded-development capabilities."),
        AIFunctionFactory.Create((Func<string, CancellationToken, Task<AliArduinoOperationResult>>)Arduino.SearchLibrariesAsync,
            AliCapabilityCatalog.ArduinoSearchLibrariesName,
            "Search the official Arduino Library Manager catalog for an explicit library identifier."),
        AIFunctionFactory.Create((Func<string, CancellationToken, Task<AliArduinoOperationResult>>)Arduino.InstallCoreAsync,
            AliCapabilityCatalog.ArduinoInstallCoreName,
            "Install an explicit Arduino board core after approval."),
        AIFunctionFactory.Create((Func<string, string?, CancellationToken, Task<AliArduinoOperationResult>>)Arduino.InstallLibraryAsync,
            AliCapabilityCatalog.ArduinoInstallLibraryName,
            "Install an explicit Arduino library and optional version after approval."),
        AIFunctionFactory.Create((Func<string, string, string, CancellationToken, Task<AliArduinoOperationResult>>)Arduino.CreateAndCompileAsync,
            AliCapabilityCatalog.ArduinoCreateCompileName,
            "Create one new compile-ready Arduino .ino file at an approved Desktop, Documents, Downloads, Exports, or Workspace path and compile it for an explicit FQBN in the same verified operation. Accept either a virtual path such as Desktop/Blink/Blink.ino or an absolute path already inside an approved root. The sketch filename must match its parent folder. This creates missing sketch folders and never overwrites an existing file."),
        AIFunctionFactory.Create((Func<string, string, CancellationToken, Task<AliArduinoOperationResult>>)Arduino.CompileAsync,
            AliCapabilityCatalog.ArduinoCompileName,
            "Compile an approved Arduino sketch for an explicit fully-qualified board name."),
        AIFunctionFactory.Create((Func<string, string, string, CancellationToken, Task<AliArduinoOperationResult>>)Arduino.UploadAsync,
            AliCapabilityCatalog.ArduinoUploadName,
            "Upload already-selected Arduino firmware to an explicit board and port after approval; never guesses hardware."),
        AIFunctionFactory.Create((Func<string, AliArduinoOperationResult>)Arduino.OpenIde,
            AliCapabilityCatalog.ArduinoOpenIdeName,
            "Open an approved sketch in the installed Arduino IDE."),
        AIFunctionFactory.Create((Func<IReadOnlyList<AliRaspberryPiLibrary>>)RaspberryPi.GetLibraryCatalog,
            AliCapabilityCatalog.RaspberryPiLibrariesName,
            "Return Ali's Raspberry Pi Python, C/C++, GPIO, bus, camera, and Pico library catalog."),
        AIFunctionFactory.Create((Func<string, string, int, CancellationToken, Task<AliRaspberryPiOperationResult>>)RaspberryPi.ProbeAsync,
            AliCapabilityCatalog.RaspberryPiProbeName,
            "Probe an explicit Raspberry Pi/Linux host through bounded non-interactive SSH."),
        AIFunctionFactory.Create((Func<string, string, int, CancellationToken, Task<AliRaspberryPiOperationResult>>)RaspberryPi.InspectLibrariesAsync,
            AliCapabilityCatalog.RaspberryPiInspectLibrariesName,
            "Inspect installed GPIO, SPI, I2C, and camera libraries on an explicit Raspberry Pi through SSH."),
        AIFunctionFactory.Create((Func<string, string, int, string, CancellationToken, Task<AliRaspberryPiOperationResult>>)RaspberryPi.SearchPackagesAsync,
            AliCapabilityCatalog.RaspberryPiSearchPackagesName,
            "Search Raspberry Pi OS development packages on an explicit host without installing anything."),
        AIFunctionFactory.Create((Func<string, string, string, int, string, CancellationToken, Task<AliRaspberryPiOperationResult>>)RaspberryPi.DeployAsync,
            AliCapabilityCatalog.RaspberryPiDeployName,
            "Copy an approved project to an explicit Raspberry Pi path and run its bounded build check after approval."),
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
            (Func<string, CancellationToken, Task<AliLanguageOperationResult>>)ExecuteCodingAnalyzeAsync,
            AliCapabilityCatalog.CodingAnalyzeProjectName,
            "Run the registered language provider's semantic and static analysis for an approved project."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<AliLanguageOperationResult>>)ExecuteCodingFormatAsync,
            AliCapabilityCatalog.CodingFormatProjectName,
            "Format an approved project through its registered language provider after user approval."),
        AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<AliLanguageOperationResult>>)ExecuteCodingBuildAsync,
            AliCapabilityCatalog.CodingBuildProjectName,
            "Build an approved project through its detected C#, Python, web, Java, or C++ provider."),
        AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<AliLanguageOperationResult>>)ExecuteCodingTestAsync,
            AliCapabilityCatalog.CodingTestProjectName,
            "Run an approved project's native test system through its registered language provider."),
        AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<AliLanguageOperationResult>>)ExecuteCodingRunAsync,
            AliCapabilityCatalog.CodingRunProjectName,
            "Execute an approved project through its detected language provider with a bounded runtime after user approval."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<AliCrossLanguageArchitectureReport>>)CrossLanguageArchitecture.InspectAsync,
            AliCapabilityCatalog.CodingInspectArchitectureName,
            "Build a bounded evidence-backed dependency graph, cycle list, hotspot ranking, and Mermaid view across C#, Python, web, Java, and C/C++ source."),
        AIFunctionFactory.Create(
            (Func<string, string, int?, int?, CancellationToken, Task<AliCodeContextReport>>)Operations.BuildContextAsync,
            AliCapabilityCatalog.CodingBuildContextName,
            "Narrow a very large approved project into ranked, bounded source snippets relevant to one explicit question or symbol."),
        AIFunctionFactory.Create(
            (Func<string, string, string?, CancellationToken, Task<AliExternalServiceProbeResult>>)Operations.ProbeHttpAsync,
            AliCapabilityCatalog.CodingProbeServiceName,
            "Send an approved bounded GET or HEAD request to an explicit HTTP service and return status plus a capped response preview. Never sends credentials or request bodies."),
        AIFunctionFactory.Create(
            (Func<string, int, AliRuntimeProcessSnapshot>)Operations.InspectProcess,
            AliCapabilityCatalog.CodingInspectProcessName,
            "Capture a live point-in-time CPU, memory, thread, and responsiveness snapshot for an explicitly identified process after user approval."),
        AIFunctionFactory.Create(
            (Func<string, string, CancellationToken, Task<DotNetCreateProjectResult>>)ExecuteDotNetCreateAsync,
            AliCapabilityCatalog.DotNetCreateProjectName,
            "Create a new C# project scaffold in an empty approved folder. projectPath must be a virtual .csproj path such as Desktop/TicTacToe/TicTacToe.csproj; template must be wpf or console. After success, write the complete requested source before building."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<RoslynAnalysisResult>>)Queries.AnalyzeAsync,
            AliCapabilityCatalog.RoslynAnalyzeProjectName,
            "Load an approved C# project with Roslyn and return semantic compiler diagnostics with exact source locations."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<RoslynFormatResult>>)ExecuteRoslynFormatAsync,
            AliCapabilityCatalog.RoslynFormatProjectName,
            "Format every C# document in an approved project with Roslyn."),
        AIFunctionFactory.Create(
            (Func<string, string, CancellationToken, Task<RoslynSymbolResult>>)Queries.FindSymbolAsync,
            AliCapabilityCatalog.RoslynFindSymbolName,
            "Find C# type or member declarations semantically with Roslyn."),
        AIFunctionFactory.Create(
            (Func<string, string, int, int, CancellationToken, Task<RoslynCompletionResult>>)Queries.GetCompletionsAsync,
            AliCapabilityCatalog.RoslynGetCompletionsName,
            "Return Roslyn IntelliSense completion candidates at a one-based C# source position."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<RoslynSolutionOverviewResult>>)Queries.InspectSolutionAsync,
            AliCapabilityCatalog.RoslynInspectSolutionName,
            "Inspect an approved .csproj, .sln, or .slnx and return its C# project graph, references, target frameworks, and document counts."),
        AIFunctionFactory.Create(
            (Func<string, string, CancellationToken, Task<RoslynDocumentResult>>)Queries.InspectDocumentAsync,
            AliCapabilityCatalog.RoslynInspectDocumentName,
            "Return Roslyn's semantic outline, live diagnostics, and classified source spans for one C# document."),
        AIFunctionFactory.Create(
            (Func<string, string, int, int, CancellationToken, Task<RoslynPositionResult>>)Queries.InspectPositionAsync,
            AliCapabilityCatalog.RoslynInspectPositionName,
            "Return Roslyn hover text, definitions, and invocation signatures at a one-based C# source position."),
        AIFunctionFactory.Create(
            (Func<string, string, int, int, CancellationToken, Task<RoslynReferenceResult>>)Queries.FindReferencesAsync,
            AliCapabilityCatalog.RoslynFindReferencesName,
            "Find every semantic reference to the C# symbol at a one-based source position across a project or solution."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<AliRoslynTargetInspection>>)ActionDeck.InspectTargetAsync,
            AliCapabilityCatalog.RoslynInspectTargetName,
            "Load one exact approved C# project or solution through the canonical MSBuild workspace and return its bounded semantic identity and diagnostics."),
        AIFunctionFactory.Create(
            (Func<string, string, int, int, CancellationToken, Task<AliRoslynActionListResult>>)ActionDeck.ListActionsAsync,
            AliCapabilityCatalog.RoslynListActionsName,
            "Discover exact provider-backed Roslyn actions at a one-based source position; execute only a returned cryptographic action identity."),
        AIFunctionFactory.Create(
            (Func<string, string, int, int, string, string, CancellationToken, Task<AliRoslynActionPreview>>)ActionDeck.PreviewActionAsync,
            AliCapabilityCatalog.RoslynPreviewActionName,
            "Run one exact discovered Roslyn action in an isolated workspace and create a protected, expiring staged changeset handle without changing canonical source."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<AliRoslynActionApplication>>)ActionPublisher.ApplyAsync,
            AliCapabilityCatalog.RoslynApplyActionName,
            "Publish only the exact non-stale, approved, preverified Action Deck handle through the all-or-reconciled source transaction."),
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<AliRoslynActionVerification>>)ActionDeck.VerifyAsync,
            AliCapabilityCatalog.RoslynVerifyChangesetName,
            "Verify one protected preview handle with isolated Roslyn diagnostics, the real staged MSBuild graph, and dependency-related tests before publication."),
        AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<DotNetBuildResult>>)ExecuteDotNetBuildAsync,
            AliCapabilityCatalog.DotNetBuildName,
            "Restore and compile an approved C# project through Microsoft's in-process MSBuild API."),
        AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<DotNetRunResult>>)ExecuteDotNetRunAsync,
            AliCapabilityCatalog.DotNetRunName,
            "Launch an already-built .NET application from an approved project after user approval."),
        AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<DotNetStopProjectResult>>)ExecuteDotNetStopAsync,
            AliCapabilityCatalog.DotNetStopProjectName,
            "Close the running target application for an approved .NET project after user approval. Use when a build reports RunningTarget, OutputLocked, MSB3021, or MSB3027; then call the build tool again."),
        AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<DotNetTestResult>>)ExecuteDotNetTestAsync,
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
        AIFunctionFactory.Create((Func<string, CancellationToken, Task<DependencyInspectionResult>>)ExecuteDependencyInspectAsync,
            AliCapabilityCatalog.DotNetDependencyInspectName, "Inspect exact PackageReferences, lock-file state, and NuGet vulnerability/deprecation audit results."),
        AIFunctionFactory.Create((Func<string, string, string, string?, CancellationToken, Task<DependencyChangeResult>>)Dependencies.PreviewChangeAsync,
            AliCapabilityCatalog.DotNetDependencyPreviewName, "Preview an exact add, update, or remove PackageReference edit without writing the project."),
        AIFunctionFactory.Create((Func<string, string, string, string?, CancellationToken, Task<DependencyChangeResult>>)ExecuteDependencyApplyAsync,
            AliCapabilityCatalog.DotNetDependencyApplyName, "Apply a previously considered exact PackageReference add, update, or remove after approval."),
        AIFunctionFactory.Create((Func<string, CancellationToken, Task<SourceControlResult>>)ExecuteGitStatusAsync,
            AliCapabilityCatalog.GitStatusName, "Return authoritative Git branch and working-tree status for an approved coding target."),
        AIFunctionFactory.Create((Func<string, bool, CancellationToken, Task<SourceControlResult>>)ExecuteGitDiffAsync,
            AliCapabilityCatalog.GitDiffName, "Return the current staged or unstaged Git patch for review."),
        AIFunctionFactory.Create((Func<string, int, CancellationToken, Task<SourceControlResult>>)SourceControl.HistoryAsync,
            AliCapabilityCatalog.GitHistoryName, "Return bounded Git commit history with hashes, timestamps, authors, and subjects."),
        AIFunctionFactory.Create((Func<string, string, CancellationToken, Task<SourceControlResult>>)SourceControl.BlameAsync,
            AliCapabilityCatalog.GitBlameName, "Return Git line history for one approved project document."),
        AIFunctionFactory.Create((Func<string, string, CancellationToken, Task<SourceControlResult>>)ExecuteGitCreateBranchAsync,
            AliCapabilityCatalog.GitCreateBranchName, "Create and switch to a validated Git branch after approval."),
        AIFunctionFactory.Create((Func<string, string, CancellationToken, Task<SourceControlResult>>)ExecuteGitCommitAsync,
            AliCapabilityCatalog.GitCommitName, "Commit already-staged changes with a bounded one-line message after approval."),
        AIFunctionFactory.Create((Func<string, string, string, CancellationToken, Task<SourceControlResult>>)ExecuteGitPushAsync,
            AliCapabilityCatalog.GitPushName, "Push one validated branch to one validated remote after approval."),
        AIFunctionFactory.Create((Func<string, CancellationToken, Task<ArchitectureInspectionResult>>)ExecuteArchitectureInspectAsync,
            AliCapabilityCatalog.ArchitectureInspectName, "Build semantic project and call graphs and report project cycles."),
        AIFunctionFactory.Create((Func<string, ArchitectureBoundaryRule[], CancellationToken, Task<ArchitectureBoundaryResult>>)ExecuteArchitectureCheckAsync,
            AliCapabilityCatalog.ArchitectureCheckName, "Evaluate explicit namespace dependency boundary rules against Roslyn semantic call edges."),
        AIFunctionFactory.Create((Func<string, CancellationToken, Task<QualityScanResult>>)ExecuteQualityScanAsync,
            AliCapabilityCatalog.DotNetQualityScanName, "Run Roslyn diagnostics and bounded secret checks and write a stable SARIF quality artifact."),
        AIFunctionFactory.Create((Func<string, string?, int, CancellationToken, Task<PerformanceRunResult>>)Performance.MeasureAsync,
            AliCapabilityCatalog.DotNetPerformanceMeasureName, "Measure a built application over bounded iterations and write repeatable wall-time, CPU, and memory evidence."),
        AIFunctionFactory.Create((Func<string, string, string, CancellationToken, Task<PerformanceComparisonResult>>)Performance.CompareAsync,
            AliCapabilityCatalog.DotNetPerformanceCompareName, "Compare two saved performance evidence artifacts and report the measured regression or improvement."),
        AIFunctionFactory.Create((Func<string, int, int, CancellationToken, Task<PerformanceTraceResult>>)Performance.CaptureTraceAsync,
            AliCapabilityCatalog.DotNetPerformanceTraceName, "Capture a bounded managed EventPipe CPU/allocation trace from an approved project process."),
        AIFunctionFactory.Create((Func<string, string?, string?, CancellationToken, Task<ApplicationVerificationResult>>)ExecuteApplicationVerifyAsync,
            AliCapabilityCatalog.DotNetApplicationVerifyName, "Run an actual console, desktop, service, or loopback-web smoke test and capture visible desktop evidence when applicable."),
        AIFunctionFactory.Create((Func<string, string?, bool, CancellationToken, Task<DotNetReleaseResult>>)ExecuteReleasePublishAsync,
            AliCapabilityCatalog.DotNetReleasePublishName, "Create a bounded .NET publish folder and a cryptographic file manifest after approval."),
        AIFunctionFactory.Create((Func<string, CancellationToken, Task<EngineeringReportResult>>)Release.GenerateArchitectureReportAsync,
            AliCapabilityCatalog.DotNetArchitectureReportName, "Generate a source-backed Markdown architecture report from semantic project and call graphs."),
        AIFunctionFactory.Create((Func<string, string?, string?, bool, bool, CancellationToken, Task<AutonomousDeliveryResult>>)ExecuteDeliveryVerifyAsync,
            AliCapabilityCatalog.DotNetDeliveryVerifyName, "Run the final architecture, quality, build, test, optional app, and optional release evidence gates before claiming delivery complete. Supply testTargetPath when tests live in a separate project.")
    ];

    private Task<AliLanguageOperationResult> ExecuteCodingAnalyzeAsync(
        string targetPath,
        CancellationToken cancellationToken) =>
        CodingExecution.ExecuteProviderAnalyzeAsync(
            targetPath,
            token => MultiLanguage.AnalyzeAsync(targetPath, token),
            cancellationToken);

    private Task<AliLanguageOperationResult> ExecuteCodingFormatAsync(
        string targetPath,
        CancellationToken cancellationToken) =>
        CodingExecution.ExecuteProviderFormatAsync(
            targetPath,
            token => MultiLanguage.FormatAsync(targetPath, token),
            cancellationToken);

    private Task<AliLanguageOperationResult> ExecuteCodingBuildAsync(
        string targetPath,
        string? configuration,
        CancellationToken cancellationToken) =>
        CodingExecution.ExecuteProviderBuildAsync(
            targetPath,
            configuration,
            token => MultiLanguage.BuildAsync(targetPath, configuration, token),
            cancellationToken);

    private Task<AliLanguageOperationResult> ExecuteCodingTestAsync(
        string targetPath,
        string? configuration,
        CancellationToken cancellationToken) =>
        CodingExecution.ExecuteProviderTestAsync(
            targetPath,
            configuration,
            token => MultiLanguage.TestAsync(targetPath, configuration, token),
            cancellationToken);

    private Task<AliLanguageOperationResult> ExecuteCodingRunAsync(
        string targetPath,
        string? configuration,
        CancellationToken cancellationToken) =>
        CodingExecution.ExecuteProviderRunAsync(
            targetPath,
            configuration,
            token => MultiLanguage.RunAsync(targetPath, configuration, token),
            cancellationToken);

    private Task<DotNetCreateProjectResult> ExecuteDotNetCreateAsync(
        string projectPath,
        string template,
        CancellationToken cancellationToken) =>
        CodingExecution.ExecuteDotNetCreateAsync(
            projectPath,
            template,
            token => ProjectScaffolder.CreateAsync(projectPath, template, token),
            cancellationToken);

    private Task<RoslynFormatResult> ExecuteRoslynFormatAsync(
        string projectPath,
        CancellationToken cancellationToken) =>
        CodingExecution.ExecuteRoslynFormatAsync(
            projectPath,
            token => Tools.FormatAsync(projectPath, token),
            cancellationToken);

    private Task<DotNetBuildResult> ExecuteDotNetBuildAsync(
        string projectPath,
        string? configuration,
        CancellationToken cancellationToken) =>
        CodingExecution.ExecuteDotNetBuildAsync(
            projectPath,
            configuration,
            token => Tools.BuildAsync(projectPath, configuration, token),
            cancellationToken);

    private Task<DotNetRunResult> ExecuteDotNetRunAsync(
        string projectPath,
        string? configuration,
        CancellationToken cancellationToken) =>
        CodingExecution.ExecuteDotNetRunAsync(
            projectPath,
            configuration,
            token => Tools.RunAsync(projectPath, configuration, token),
            cancellationToken);

    private Task<DotNetStopProjectResult> ExecuteDotNetStopAsync(
        string projectPath,
        string? configuration,
        CancellationToken cancellationToken) =>
        CodingExecution.ExecuteDotNetStopAsync(
            projectPath,
            configuration,
            token => Tools.StopProjectAsync(projectPath, configuration, token),
            cancellationToken);

    private Task<DotNetTestResult> ExecuteDotNetTestAsync(
        string targetPath,
        string? configuration,
        CancellationToken cancellationToken) =>
        CodingExecution.ExecuteDotNetTestAsync(
            targetPath,
            configuration,
            token => EngineeringLoop.TestAsync(targetPath, configuration, token),
            cancellationToken);

    private Task<DotNetVerificationResult> VerifyAsync(
        string targetPath,
        string? configuration,
        CancellationToken cancellationToken) =>
        CodingExecution.ExecuteDotNetVerifyAsync(
            targetPath,
            configuration,
            token => EngineeringLoop.VerifyAsync(
                targetPath,
                configuration,
                Tools.BuildAsync,
                token),
            cancellationToken);

    private Task<DependencyInspectionResult> ExecuteDependencyInspectAsync(
        string projectPath,
        CancellationToken cancellationToken) =>
        CodingExecution.ExecuteDependencyInspectAsync(
            projectPath,
            token => Dependencies.InspectAsync(projectPath, token),
            cancellationToken);

    private Task<DependencyChangeResult> ExecuteDependencyApplyAsync(
        string projectPath,
        string action,
        string packageId,
        string? version,
        CancellationToken cancellationToken) =>
        CodingExecution.ExecuteDependencyApplyAsync(
            projectPath,
            action,
            packageId,
            version,
            token => Dependencies.ApplyChangeAsync(
                projectPath,
                action,
                packageId,
                version,
                token),
            cancellationToken);

    private Task<SourceControlResult> ExecuteGitStatusAsync(
        string targetPath,
        CancellationToken cancellationToken) =>
        GitExecution.ExecuteStatusAsync(
            targetPath,
            token => SourceControl.StatusAsync(targetPath, token),
            cancellationToken);

    private Task<SourceControlResult> ExecuteGitDiffAsync(
        string targetPath,
        bool staged,
        CancellationToken cancellationToken) =>
        GitExecution.ExecuteDiffAsync(
            targetPath,
            staged,
            token => SourceControl.DiffAsync(targetPath, staged, token),
            cancellationToken);

    private Task<SourceControlResult> ExecuteGitCreateBranchAsync(
        string targetPath,
        string branchName,
        CancellationToken cancellationToken) =>
        GitExecution.ExecuteCreateBranchAsync(
            targetPath,
            branchName,
            token => SourceControl.CreateBranchAsync(targetPath, branchName, token),
            cancellationToken);

    private Task<SourceControlResult> ExecuteGitCommitAsync(
        string targetPath,
        string message,
        CancellationToken cancellationToken) =>
        GitExecution.ExecuteCommitAsync(
            targetPath,
            message,
            token => SourceControl.CommitAsync(targetPath, message, token),
            cancellationToken);

    private Task<SourceControlResult> ExecuteGitPushAsync(
        string targetPath,
        string remote,
        string branchName,
        CancellationToken cancellationToken) =>
        GitExecution.ExecutePushAsync(
            targetPath,
            remote,
            branchName,
            token => SourceControl.PushAsync(targetPath, remote, branchName, token),
            cancellationToken);

    private Task<ArchitectureInspectionResult> ExecuteArchitectureInspectAsync(
        string targetPath,
        CancellationToken cancellationToken) =>
        DevOpsExecution.ExecuteArchitectureInspectAsync(
            targetPath,
            token => Architecture.InspectAsync(targetPath, token),
            cancellationToken);

    private Task<ArchitectureBoundaryResult> ExecuteArchitectureCheckAsync(
        string targetPath,
        ArchitectureBoundaryRule[] rules,
        CancellationToken cancellationToken) =>
        DevOpsExecution.ExecuteArchitectureCheckAsync(
            targetPath,
            rules,
            token => Architecture.CheckBoundariesAsync(targetPath, rules, token),
            cancellationToken);

    private Task<QualityScanResult> ExecuteQualityScanAsync(
        string projectPath,
        CancellationToken cancellationToken) =>
        DevOpsExecution.ExecuteQualityScanAsync(
            projectPath,
            token => Quality.ScanAsync(projectPath, token),
            cancellationToken);

    private Task<ApplicationVerificationResult> ExecuteApplicationVerifyAsync(
        string projectPath,
        string? configuration,
        string? healthUrl,
        CancellationToken cancellationToken) =>
        DevOpsExecution.ExecuteApplicationVerifyAsync(
            projectPath,
            configuration,
            healthUrl,
            token => Verification.SmokeTestAsync(
                projectPath,
                configuration,
                healthUrl,
                token),
            cancellationToken);

    private Task<DotNetReleaseResult> ExecuteReleasePublishAsync(
        string projectPath,
        string? runtimeIdentifier,
        bool selfContained,
        CancellationToken cancellationToken) =>
        DevOpsExecution.ExecuteReleasePublishAsync(
            projectPath,
            runtimeIdentifier,
            selfContained,
            token => Release.PublishAsync(
                projectPath,
                runtimeIdentifier,
                selfContained,
                token),
            cancellationToken);

    private Task<AutonomousDeliveryResult> ExecuteDeliveryVerifyAsync(
        string projectPath,
        string? testTargetPath,
        string? configuration,
        bool verifyApplication,
        bool publishRelease,
        CancellationToken cancellationToken) =>
        DevOpsExecution.ExecuteDeliveryVerifyAsync(
            projectPath,
            testTargetPath,
            configuration,
            verifyApplication,
            publishRelease,
            token => Delivery.VerifyDeliveryAsync(
                projectPath,
                testTargetPath,
                configuration,
                verifyApplication,
                publishRelease,
                token),
            cancellationToken);

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
        Operations.Dispose();
        await Debugger.DisposeAsync().ConfigureAwait(false);
    }
}
