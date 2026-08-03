using System.Text;
using Ali.Modules.Mcp;
using Microsoft.Agents.AI;

namespace Ali.Modules.Coordinator;

public static class AliCapabilityCatalog
{
    public const string ListAvailableToolsName = "list_available_tools";
    public const string SemanticDiscoverToolsName = "discover_capabilities";
    public const string GetActiveUserProfileName = "get_active_user_profile";
    public const string RecallUserMemoryName = "recall_user_memory";
    public const string RememberCurrentUserName = "remember_for_current_user";
    public const string CorrectCurrentUserMemoryName = "correct_current_user_memory";
    public const string ForgetCurrentUserMemoryName = "forget_current_user_memory";
    public const string MutateParticipantMemoryName = "mutate_participant_memory";
    public const string ConsentParticipantMemoryProposalName = "consent_participant_memory_proposal";
    public const string ReconcileParticipantMemoryMutationName = "reconcile_participant_memory_mutation";
    public const string ListCurrentUserMemoriesName = "list_current_user_memories";
    public const string SearchCurrentWebName = "search_current_web";
    public const string CreateGoogleMapsDirectionsLinkName = "maps_create_directions_link";
    public const string ResearchWebName = "research_web";
    public const string SearchLocalLibraryName = "search_local_library";
    public const string CreateCalendarEventName = "create_calendar_event";
    public const string GetAssistantIdentityName = "get_assistant_identity";
    public const string GetCurrentLocalTimeName = "get_current_local_time";
    public const string FileWriteName = "file_access_write";
    public const string FileReadName = "file_access_read";
    public const string FileDeleteName = "file_access_delete";
    public const string FileListName = "file_access_ls";
    public const string FileSearchName = "file_access_grep";
    public const string FileReplaceName = "file_access_replace";
    public const string FileReplaceLinesName = "file_access_replace_lines";
    public const string FileMoveName = "file_access_move";
    public const string FileCopyName = "file_access_copy";
    public const string FileCreateDirectoryName = "file_access_create_directory";
    public const string FileMetadataName = "file_access_metadata";
    public const string ArchiveCreateName = "archive_create";
    public const string ArchiveListName = "archive_list";
    public const string ArchiveExtractName = "archive_extract";
    public const string WorkMemoryWriteName = "file_memory_write";
    public const string WorkMemoryReadName = "file_memory_read";
    public const string WorkMemoryDeleteName = "file_memory_delete";
    public const string WorkMemoryListName = "file_memory_ls";
    public const string WorkMemorySearchName = "file_memory_grep";
    public const string WorkMemoryReplaceName = "file_memory_replace";
    public const string WorkMemoryReplaceLinesName = "file_memory_replace_lines";
    public const string GetAgentModeName = "mode_get";
    public const string SetAgentModeName = "mode_set";
    public const string LoadAgentSkillName = "load_skill";
    public const string ReadAgentSkillResourceName = "read_skill_resource";
    public const string RunAgentSkillScriptName = "run_skill_script";
    public const string CodingListCapabilitiesName = "coding_list_capabilities";
    public const string CodingAgentStatusName = "coding_agent_status";
    public const string CodingAgentExecuteName = "coding_agent_execute";
    public const string CodingInspectProjectName = "coding_inspect_project";
    public const string CodingIndexProjectName = "coding_index_project";
    public const string CodingSearchSymbolsName = "coding_search_symbols";
    public const string CodingAnalyzeProjectName = "coding_analyze_project";
    public const string CodingFormatProjectName = "coding_format_project";
    public const string CodingBuildProjectName = "coding_build_project";
    public const string CodingTestProjectName = "coding_test_project";
    public const string CodingRunProjectName = "coding_run_project";
    public const string CodingInspectArchitectureName = "coding_inspect_architecture";
    public const string CodingBuildContextName = "coding_build_context";
    public const string CodingProbeServiceName = "coding_probe_http_service";
    public const string CodingInspectProcessName = "coding_inspect_process";
    public const string VisualStudioInspectName = "visual_studio_inspect";
    public const string VisualStudioBuildName = "visual_studio_build";
    public const string VisualStudioOpenName = "visual_studio_open";
    public const string GnuNativeInspectName = "native_gnu_inspect";
    public const string GnuNativeExecuteName = "native_gnu_execute";
    public const string ArduinoInspectName = "arduino_inspect";
    public const string ArduinoSearchLibrariesName = "arduino_search_libraries";
    public const string ArduinoInstallCoreName = "arduino_install_core";
    public const string ArduinoInstallLibraryName = "arduino_install_library";
    public const string ArduinoCreateCompileName = "arduino_create_and_compile";
    public const string ArduinoCompileName = "arduino_compile";
    public const string ArduinoUploadName = "arduino_upload";
    public const string ArduinoOpenIdeName = "arduino_open_ide";
    public const string RaspberryPiLibrariesName = "raspberry_pi_library_catalog";
    public const string RaspberryPiProbeName = "raspberry_pi_probe";
    public const string RaspberryPiInspectLibrariesName = "raspberry_pi_inspect_libraries";
    public const string RaspberryPiSearchPackagesName = "raspberry_pi_search_packages";
    public const string RaspberryPiDeployName = "raspberry_pi_deploy";
    public const string DotNetCreateProjectName = "dotnet_create_project";
    public const string RoslynAnalyzeProjectName = "roslyn_analyze_project";
    public const string RoslynFormatProjectName = "roslyn_format_project";
    public const string RoslynFindSymbolName = "roslyn_find_symbol";
    public const string RoslynGetCompletionsName = "roslyn_get_completions";
    public const string RoslynInspectSolutionName = "roslyn_inspect_solution";
    public const string RoslynInspectDocumentName = "roslyn_inspect_document";
    public const string RoslynInspectPositionName = "roslyn_inspect_position";
    public const string RoslynFindReferencesName = "roslyn_find_references";
    public const string RoslynPreviewRenameName = "roslyn_preview_rename";
    public const string RoslynApplyRenameName = "roslyn_apply_rename";
    public const string RoslynInspectTargetName = "roslyn_inspect_target";
    public const string RoslynListActionsName = "roslyn_list_actions";
    public const string RoslynPreviewActionName = "roslyn_preview_action";
    public const string RoslynApplyActionName = "roslyn_apply_action";
    public const string RoslynVerifyChangesetName = "roslyn_verify_changeset";
    public const string DotNetBuildName = "dotnet_build_project";
    public const string DotNetRunName = "dotnet_run_project";
    public const string DotNetStopProjectName = "dotnet_stop_project";
    public const string DotNetTestName = "dotnet_test_project";
    public const string DotNetVerifyName = "dotnet_verify_project";
    public const string DotNetDebugLaunchName = "dotnet_debug_launch";
    public const string DotNetDebugAttachName = "dotnet_debug_attach";
    public const string DotNetDebugInspectName = "dotnet_debug_inspect";
    public const string DotNetDebugEvaluateName = "dotnet_debug_evaluate";
    public const string DotNetDebugBreakpointsName = "dotnet_debug_set_breakpoints";
    public const string DotNetDebugControlName = "dotnet_debug_control";
    public const string DotNetDebugStopName = "dotnet_debug_stop";
    public const string DotNetDebugDiagnosticsHandoffName = "dotnet_debug_diagnostics_handoff";
    public const string DotNetDependencyInspectName = "dotnet_dependency_inspect";
    public const string DotNetDependencyPreviewName = "dotnet_dependency_preview";
    public const string DotNetDependencyApplyName = "dotnet_dependency_apply";
    public const string GitStatusName = "git_status";
    public const string GitDiffName = "git_diff";
    public const string GitHistoryName = "git_history";
    public const string GitBlameName = "git_blame";
    public const string GitCreateBranchName = "git_create_branch";
    public const string GitCommitName = "git_commit";
    public const string GitPushName = "git_push";
    public const string ArchitectureInspectName = "architecture_inspect";
    public const string ArchitectureCheckName = "architecture_check_boundaries";
    public const string DotNetQualityScanName = "dotnet_quality_scan";
    public const string DotNetPerformanceMeasureName = "dotnet_performance_measure";
    public const string DotNetPerformanceCompareName = "dotnet_performance_compare";
    public const string DotNetPerformanceTraceName = "dotnet_performance_trace";
    public const string DotNetApplicationVerifyName = "dotnet_application_verify";
    public const string DotNetReleasePublishName = "dotnet_release_publish";
    public const string DotNetArchitectureReportName = "dotnet_architecture_report";
    public const string DotNetDeliveryVerifyName = "dotnet_delivery_verify";
    public const string ConsultSoftwareEngineerName = "consult_software_engineer";
    public const string ConsultResearcherName = "consult_researcher";
    public const string ConsultOfficeSpecialistName = "consult_office_artifact_specialist";
    public const string RunResearchArtifactWorkflowName = "run_research_artifact_workflow";
    public const string RunProgrammingGroupChatName = "run_programming_group_chat";
    public const string RunMagenticOrchestrationName = "run_magentic_orchestration";
    public const string ListRecoverableWorkflowsName = "list_recoverable_workflows";
    public const string ResumeWorkflowCheckpointName = "resume_workflow_checkpoint";

    public static IReadOnlyList<CoordinatorCapability> Tools { get; } =
    [
        new(ListAvailableToolsName, "Return Ali's exact currently registered model-callable tool catalog."),
        new(SemanticDiscoverToolsName, "Semantically retrieve the most relevant live tool drawers for a described unmet need without interpreting the user's request by keyword."),
        new(GetActiveUserProfileName, "Return the explicitly selected local user's identity profile as authoritative data."),
        new(RecallUserMemoryName, "Recall attributable participant memory against the immutable admitted roster and trusted read authority."),
        new(MutateParticipantMemoryName, "Submit one typed participant-memory add, correction, dispute, revoke, archive, or exact delete proposal through trusted consent and authority boundaries."),
        new(ConsentParticipantMemoryProposalName, "Record the explicitly selected participant's short-lived approval for one exact typed participant-memory proposal so every attributed participant can approve separately."),
        new(ReconcileParticipantMemoryMutationName, "Inspect one exact durable participant-memory mutation request ID and reconcile only its existing journal receipt without reapplying the operation."),
        new(ListCurrentUserMemoriesName, "List bounded participant memories authorized for the immutable admitted roster."),
        new(SearchCurrentWebName, "Search the configured live internet backends for current or source-dependent information."),
        new(CreateGoogleMapsDirectionsLinkName, "Create a Google Maps directions handoff from explicit origin, destination, and ordered waypoint queries without inventing route details.", "Ali navigation tools"),
        new(ResearchWebName, "Run provider-managed, multi-source web research for complex nested or comparative questions."),
        new(SearchLocalLibraryName, "Search the user's indexed local RAG library and reference documents."),
        new(CreateCalendarEventName, "Create a persistent local calendar event with a portable iCalendar file and an operating-system notification that survives Ali closing."),
        new(GetAssistantIdentityName, "Return Ali's configured local assistant identity."),
        new(GetCurrentLocalTimeName, "Return the authoritative local computer date, time, and time zone."),
        new(FileWriteName, "Create a new text file or, after approval, overwrite an existing file in Ali's approved workstation folders.", "Microsoft Agent Framework file access"),
        new(FileReadName, "Read a text file from Ali's approved workstation folders.", "Microsoft Agent Framework file access"),
        new(FileDeleteName, "Move a file or folder from an approved workstation root into Ali's recoverable trash after approval.", "Microsoft Agent Framework file access"),
        new(FileListName, "List files and folders under Ali's approved workstation roots.", "Microsoft Agent Framework file access"),
        new(FileSearchName, "Search text inside files under Ali's approved workstation roots.", "Microsoft Agent Framework file access"),
        new(FileReplaceName, "Edit matching text in an existing file after approval.", "Microsoft Agent Framework file access"),
        new(FileReplaceLinesName, "Edit specific lines in an existing file after approval.", "Microsoft Agent Framework file access"),
        new(FileMoveName, "Rename or move an existing file between approved workstation folders after approval.", "Ali workstation file tools"),
        new(FileCopyName, "Copy a file or folder to a new path without overwriting an existing item.", "Ali workstation file tools"),
        new(FileCreateDirectoryName, "Create a folder beneath an approved workstation root.", "Ali workstation file tools"),
        new(FileMetadataName, "Read file or folder metadata and optionally calculate a file SHA-256 hash.", "Ali workstation file tools"),
        new(ArchiveCreateName, "Create ZIP, TAR, TAR.GZ, GZip, or explicitly requested 7z archives without overwriting.", "Ali workstation archive tools"),
        new(ArchiveListName, "List and validate supported archive contents without extracting them.", "Ali workstation archive tools"),
        new(ArchiveExtractName, "Safely extract a supported archive into a new approved folder with traversal and size checks.", "Ali workstation archive tools"),
        new(WorkMemoryWriteName, "Write a private working note or draft for the active user and conversation, optionally with a discovery description.", "Microsoft Agent Framework file memory"),
        new(WorkMemoryReadName, "Read a private working note from the active user and conversation.", "Microsoft Agent Framework file memory"),
        new(WorkMemoryDeleteName, "Move a private working note into Ali's recoverable work-memory trash.", "Microsoft Agent Framework file memory"),
        new(WorkMemoryListName, "List private working notes and their descriptions for the active user and conversation.", "Microsoft Agent Framework file memory"),
        new(WorkMemorySearchName, "Search private working notes for the active user and conversation.", "Microsoft Agent Framework file memory"),
        new(WorkMemoryReplaceName, "Replace matching text in a private working note.", "Microsoft Agent Framework file memory"),
        new(WorkMemoryReplaceLinesName, "Edit specific lines in a private working note.", "Microsoft Agent Framework file memory"),
        new(GetAgentModeName, "Return the current framework operating mode for this agent session.", "Microsoft Agent Framework agent mode"),
        new(SetAgentModeName, "Switch the framework operating mode only when the user explicitly authorizes the change.", "Microsoft Agent Framework agent mode"),
        new(LoadAgentSkillName, "Load the instructions for one exact installed Agent Skill.", "Microsoft Agent Framework Agent Skills"),
        new(ReadAgentSkillResourceName, "Read one exact resource referenced by a loaded Agent Skill.", "Microsoft Agent Framework Agent Skills"),
        new(RunAgentSkillScriptName, "Run one exact script referenced by a loaded Agent Skill, subject to approval.", "Microsoft Agent Framework Agent Skills"),
        new(CodingListCapabilitiesName, "Return the live registered coding providers, toolchains, and shared execution/intelligence infrastructure.", "Ali multi-language coding foundation"),
        new(CodingAgentStatusName, "Report the selected external coding executor and readiness of Aider and OpenHands.", "Ali external coding agents"),
        new(CodingAgentExecuteName, "Execute a substantial approved programming objective through the selected Aider or OpenHands executor.", "Ali external coding agents"),
        new(CodingInspectProjectName, "Detect an approved project's language, manifest, provider, capabilities, and toolchains.", "Ali multi-language coding foundation"),
        new(CodingIndexProjectName, "Build a bounded cross-language structural source index.", "Ali multi-language coding foundation"),
        new(CodingSearchSymbolsName, "Search the local cross-language source index for symbols.", "Ali multi-language coding foundation"),
        new(CodingAnalyzeProjectName, "Run the detected provider's semantic and static analysis.", "Ali multi-language coding foundation"),
        new(CodingFormatProjectName, "Format a project through its detected provider after approval.", "Ali multi-language coding foundation"),
        new(CodingBuildProjectName, "Build a project through its detected language provider.", "Ali multi-language coding foundation"),
        new(CodingTestProjectName, "Run a project's native test system through its detected provider.", "Ali multi-language coding foundation"),
        new(CodingRunProjectName, "Execute a project through its detected provider with bounded runtime evidence.", "Ali shared coding operations"),
        new(CodingInspectArchitectureName, "Map cross-language dependencies, cycles, hotspots, and Mermaid architecture from local source evidence.", "Ali cross-language architecture intelligence"),
        new(CodingBuildContextName, "Select bounded, query-relevant source snippets from a large local project.", "Ali shared coding operations"),
        new(CodingProbeServiceName, "Probe an explicit external HTTP service after approval and return a bounded response preview.", "Ali shared coding operations"),
        new(CodingInspectProcessName, "Capture a live process CPU, memory, thread, and responsiveness snapshot after approval.", "Ali shared coding operations"),
        new(VisualStudioInspectName, "Inspect installed Visual Studio IDE and workload features.", "Visual Studio integration"),
        new(VisualStudioBuildName, "Build an approved solution or project with Visual Studio MSBuild and a binary log.", "Visual Studio integration"),
        new(VisualStudioOpenName, "Open an approved solution, project, or document in Visual Studio.", "Visual Studio integration"),
        new(GnuNativeInspectName, "Inspect GCC UCRT64, G++, GDB, Make, CMake, and Ninja.", "Ali GNU native tools"),
        new(GnuNativeExecuteName, "Analyze, build, test, or run an approved C/C++ project with GCC UCRT64.", "Ali GNU native tools"),
        new(ArduinoInspectName, "Inspect Arduino CLI, IDE, cores, boards, and embedded capabilities.", "Ali Arduino integration"),
        new(ArduinoSearchLibrariesName, "Search the Arduino Library Manager catalog.", "Ali Arduino integration"),
        new(ArduinoInstallCoreName, "Install an explicit Arduino board core.", "Ali Arduino integration"),
        new(ArduinoInstallLibraryName, "Install an explicit Arduino library and version.", "Ali Arduino integration"),
        new(ArduinoCreateCompileName, "Create a new Arduino sketch, including its missing folder, at an approved virtual or absolute path and compile it for an explicit board in one verified operation.", "Ali Arduino integration"),
        new(ArduinoCompileName, "Compile an approved Arduino sketch for an explicit board.", "Ali Arduino integration"),
        new(ArduinoUploadName, "Upload firmware to an explicit Arduino board and port.", "Ali Arduino integration"),
        new(ArduinoOpenIdeName, "Open an approved sketch in Arduino IDE.", "Ali Arduino integration"),
        new(RaspberryPiLibrariesName, "Return Raspberry Pi GPIO, bus, camera, Python, C/C++, and Pico library guidance.", "Ali Raspberry Pi integration"),
        new(RaspberryPiProbeName, "Probe an explicit Raspberry Pi host through bounded SSH.", "Ali Raspberry Pi integration"),
        new(RaspberryPiInspectLibrariesName, "Inspect installed development libraries on an explicit Raspberry Pi.", "Ali Raspberry Pi integration"),
        new(RaspberryPiSearchPackagesName, "Search Raspberry Pi OS development packages on an explicit host.", "Ali Raspberry Pi integration"),
        new(RaspberryPiDeployName, "Transfer and build-check an approved project on an explicit Raspberry Pi.", "Ali Raspberry Pi integration"),
        new(DotNetCreateProjectName, "Create a new WPF or console C# project in an empty approved folder after user approval.", "Ali .NET coding tools"),
        new(RoslynAnalyzeProjectName, "Load a C# project with Roslyn and return semantic compiler diagnostics.", "Microsoft Roslyn coding intelligence"),
        new(RoslynFormatProjectName, "Format every C# document in an approved project with Roslyn after user approval.", "Microsoft Roslyn coding intelligence"),
        new(RoslynFindSymbolName, "Find C# type and member declarations semantically with Roslyn.", "Microsoft Roslyn coding intelligence"),
        new(RoslynGetCompletionsName, "Return Roslyn IntelliSense completion candidates at a C# source location.", "Microsoft Roslyn coding intelligence"),
        new(RoslynInspectSolutionName, "Inspect a C# project or solution graph with Roslyn.", "Microsoft Roslyn workspace intelligence"),
        new(RoslynInspectDocumentName, "Return Roslyn outline, live diagnostics, and semantic classifications for a C# document.", "Microsoft Roslyn editor intelligence"),
        new(RoslynInspectPositionName, "Return Roslyn hover, definition, and signature information at a C# source position.", "Microsoft Roslyn editor intelligence"),
        new(RoslynFindReferencesName, "Find all semantic references to the C# symbol at a source position.", "Microsoft Roslyn solution intelligence"),
        new(RoslynInspectTargetName, "Inspect an exact C# project or solution with the loaded public Roslyn providers before selecting a semantic action.", "Ali Roslyn Action Deck"),
        new(RoslynListActionsName, "List the semantic source actions Ali's loaded Roslyn Action Deck can actually perform.", "Ali Roslyn Action Deck"),
        new(RoslynPreviewActionName, "Preview a version-bound Roslyn semantic source action and return a durable action handle without changing canonical source.", "Ali Roslyn Action Deck"),
        new(RoslynApplyActionName, "Apply a previously previewed Roslyn action through staged validation and all-or-reconciled publication after approval.", "Ali Roslyn Action Deck"),
        new(RoslynVerifyChangesetName, "Preverify an exact preview handle with isolated compiler and analyzer diagnostics, a real staged build, and relevant tests before publication.", "Ali Roslyn Action Deck"),
        new(DotNetBuildName, "Restore and compile an approved C# .csproj through Microsoft's MSBuild API after user approval.", "Microsoft Roslyn/MSBuild coding tools"),
        new(DotNetRunName, "Launch a successfully built .NET application from an approved project folder after user approval.", "Microsoft Roslyn/MSBuild coding tools"),
        new(DotNetStopProjectName, "Close the running compiled application for one approved .NET project after user approval so its output can be rebuilt.", "Microsoft Roslyn/MSBuild coding tools"),
        new(DotNetTestName, "Discover and run .NET tests with structured TRX failures and bounded timeouts.", "Ali .NET engineering loop"),
        new(DotNetVerifyName, "Build and test an approved .NET project or solution and return stable verification evidence.", "Ali .NET engineering loop"),
        new(DotNetDebugLaunchName, "Launch an approved built .NET project under the CLR debugger.", "Ali netcoredbg DAP module"),
        new(DotNetDebugAttachName, "Attach the CLR debugger to an approved project process.", "Ali netcoredbg DAP module"),
        new(DotNetDebugInspectName, "Inspect debugger threads, stack frames, locals, and stop reasons.", "Ali netcoredbg DAP module"),
        new(DotNetDebugEvaluateName, "Evaluate a C# watch expression in an active debugger frame.", "Ali netcoredbg DAP module"),
        new(DotNetDebugBreakpointsName, "Set one or more source breakpoints in an active CLR debugger session.", "Ali netcoredbg DAP module"),
        new(DotNetDebugControlName, "Continue, pause, and step an active CLR debugger session.", "Ali netcoredbg DAP module"),
        new(DotNetDebugStopName, "Terminate an active CLR debugger session and debuggee.", "Ali netcoredbg DAP module"),
        new(DotNetDebugDiagnosticsHandoffName, "Return a safe debuggee process handoff for coverage and profiling modules.", "Ali netcoredbg DAP module"),
        new(DotNetDependencyInspectName, "Inspect direct packages, lock state, and NuGet vulnerability/deprecation evidence.", "Ali dependency engineering"),
        new(DotNetDependencyPreviewName, "Preview an exact PackageReference add, update, or remove without writing.", "Ali dependency engineering"),
        new(DotNetDependencyApplyName, "Apply an exact PackageReference add, update, or remove after approval.", "Ali dependency engineering"),
        new(GitStatusName, "Inspect authoritative Git branch and working-tree status.", "Ali source-control engineering"),
        new(GitDiffName, "Inspect staged or unstaged Git patches.", "Ali source-control engineering"),
        new(GitHistoryName, "Inspect bounded commit history.", "Ali source-control engineering"),
        new(GitBlameName, "Inspect line-level Git history for an approved project document.", "Ali source-control engineering"),
        new(GitCreateBranchName, "Create and switch to a validated Git branch after approval.", "Ali source-control engineering"),
        new(GitCommitName, "Commit already-staged changes after approval.", "Ali source-control engineering"),
        new(GitPushName, "Push a validated branch and remote after approval.", "Ali source-control engineering"),
        new(ArchitectureInspectName, "Build semantic project/call graphs and identify project cycles.", "Ali architecture engineering"),
        new(ArchitectureCheckName, "Check explicit namespace dependency boundaries semantically.", "Ali architecture engineering"),
        new(DotNetQualityScanName, "Run Roslyn/secret quality checks and produce SARIF evidence.", "Ali quality engineering"),
        new(DotNetPerformanceMeasureName, "Measure application wall time, CPU time, and peak memory over bounded iterations.", "Ali performance engineering"),
        new(DotNetPerformanceCompareName, "Compare two saved performance evidence artifacts for regressions.", "Ali performance engineering"),
        new(DotNetPerformanceTraceName, "Capture a bounded EventPipe CPU/allocation trace from an approved project process.", "Ali performance engineering"),
        new(DotNetApplicationVerifyName, "Smoke-test the actual console, desktop, service, or loopback-web application and capture visible evidence.", "Ali application verification"),
        new(DotNetReleasePublishName, "Create a .NET publish folder with a cryptographic file manifest after approval.", "Ali release engineering"),
        new(DotNetArchitectureReportName, "Generate a source-backed Markdown architecture report.", "Ali release engineering"),
        new(DotNetDeliveryVerifyName, "Run final architecture, quality, build, test, application, and release evidence gates.", "Ali autonomous delivery"),
        new(ConsultSoftwareEngineerName, "Consult Ali's private synchronous software-engineering specialist; Ali remains the only user-facing personality.", "Microsoft Agent Framework agent as tool"),
        new(ConsultResearcherName, "Consult Ali's private synchronous evidence-research specialist; Ali remains the only user-facing personality.", "Microsoft Agent Framework agent as tool"),
        new(ConsultOfficeSpecialistName, "Consult Ali's private synchronous office-artifact specialist; Ali remains the only user-facing personality.", "Microsoft Agent Framework agent as tool"),
        new(RunResearchArtifactWorkflowName, "Run Ali's synchronous Researcher-to-Office sequential workflow for evidence-backed artifact drafting.", "Microsoft Agent Framework workflow"),
        new(RunProgrammingGroupChatName, "Run Ali's bounded four-turn programming maker/checker group chat; Ali remains the final actor and voice.", "Microsoft Agent Framework workflow"),
        new(RunMagenticOrchestrationName, "Run bounded Magentic orchestration for eligible open-ended multi-domain work, subject to the configured activation policy.", "Microsoft Agent Framework workflow"),
        new(ListRecoverableWorkflowsName, "List interrupted Agent Framework workflows that can be resumed from durable local checkpoints.", "Microsoft Agent Framework recovery"),
        new(ResumeWorkflowCheckpointName, "Resume one interrupted Agent Framework workflow from its latest durable local checkpoint only after the user explicitly asks.", "Microsoft Agent Framework recovery")
    ];

    public static CoordinatorCapabilityResult ListAvailableTools() =>
        ListAvailableTools(additionalTools: [], new AgentOrchestrationSettings());

    public static CoordinatorCapabilityResult ListAvailableTools(
        AgentOrchestrationSettings orchestrationSettings) =>
        ListAvailableTools(additionalTools: [], orchestrationSettings);

    public static CoordinatorCapabilityResult ListAvailableTools(McpClientManager mcpClients)
        => ListAvailableTools(mcpClients, new AgentOrchestrationSettings());

    public static CoordinatorCapabilityResult ListAvailableTools(
        McpClientManager mcpClients,
        AgentOrchestrationSettings orchestrationSettings)
    {
        ArgumentNullException.ThrowIfNull(mcpClients);
        ArgumentNullException.ThrowIfNull(orchestrationSettings);
        return ListAvailableTools(additionalTools: [], orchestrationSettings);
    }

    private static CoordinatorCapabilityResult ListAvailableTools(
        IReadOnlyList<CoordinatorCapability> additionalTools,
        AgentOrchestrationSettings orchestrationSettings)
    {
        var nativeTools = VisibleNativeTools(orchestrationSettings);
        var allTools = nativeTools.Concat(additionalTools).ToList();
        return
        new(
            $"Ali has {allTools.Count} configured model-callable tools. This structured inventory is authoritative. Incoming MCP tools become model-callable only after they pass Ali's canonical registration, saved-policy, schema, permission, and live-availability boundary.",
            allTools);
    }

    public static string BuildPromptManifest(AgentOrchestrationSettings? orchestrationSettings = null)
    {
        var settings = (orchestrationSettings ?? new AgentOrchestrationSettings()).Normalize();
        var visibleTools = VisibleNativeTools(settings);
        var manifest = new StringBuilder()
            .AppendLine("REGISTERED MODEL-CALLABLE TOOLS (authoritative; these are the only tools you may claim to have):");
        foreach (var tool in visibleTools)
        {
            manifest.Append("- ")
                .Append(tool.Name)
                .Append(": ")
                .AppendLine(tool.Description);
        }

        manifest.Append("Incoming external MCP tools are not model-callable until Ali can publish canonical metadata, approval semantics, and live availability for them; never claim that a configured mcp_ tool is available merely because it appears in MCP settings. "
            + "Voice playback is an application output setting, not a model-callable tool. "
            + "Never claim calendar, email, arbitrary file-system, shell, camera, or generic browser-control access unless an enabled tool with that exact capability appears in the current turn.");
        return manifest.ToString();
    }

    internal static IEnumerable<CoordinatorCapability> VisibleNativeTools(AgentOrchestrationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Tools.Where(tool => !AliProductionCapabilityCatalog.IsRetiredToolName(tool.Name));
    }
}
