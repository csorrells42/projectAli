using System.Security.Cryptography;
using System.Text;
using Ali.Modules.Coordinator;
using Microsoft.Extensions.AI;

namespace Ali.Modules.ToolDiscovery;

/// <summary>
/// Mechanical live-registry metadata. These exact memberships do not interpret user text;
/// semantic similarity proposes drawers and the model alone chooses the next action.
/// </summary>
internal static class SemanticToolBuckets
{
    private const int MaximumDirectoryDescriptionCharacters = 180;
    private const int MaximumLiveToolDirectoryDescriptionCharacters = 64;
    private const string ExternalMcpServerMarker = "External MCP server: ";

    public static IReadOnlyList<ToolBucketDefinition> Create(
        IReadOnlyList<AIFunctionDeclaration> liveTools,
        bool includeDisabled = false)
    {
        var definitions = KnownDefinitions().ToList();
        var assigned = definitions.SelectMany(bucket => bucket.ToolNames).ToHashSet(StringComparer.Ordinal);
        var externalMcpGroups = liveTools
            .Where(tool => !assigned.Contains(tool.Name))
            .Select(tool => (Tool: tool, Server: ReadExternalMcpServer(tool.Description)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Server))
            .GroupBy(item => item.Server!, StringComparer.OrdinalIgnoreCase);
        foreach (var group in externalMcpGroups)
        {
            var groupedTools = group.Select(item => item.Tool).ToArray();
            definitions.Add(new ToolBucketDefinition(
                "mcp-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(group.Key)))[..12].ToLowerInvariant(),
                $"{group.Key} MCP tools",
                $"All callable operations from the {group.Key} external MCP server. The operations stay together so one model turn can inspect state, perform an action, and verify the result.",
                groupedTools.Select(tool => tool.Name).ToArray()));
            assigned.UnionWith(groupedTools.Select(tool => tool.Name));
        }

        foreach (var tool in liveTools.Where(tool => !assigned.Contains(tool.Name)))
        {
            definitions.Add(new ToolBucketDefinition(
                "live-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tool.Name)))[..12].ToLowerInvariant(),
                tool.Name,
                tool.Description ?? $"Live registered capability {tool.Name}.",
                [tool.Name]));
        }

        var liveNames = liveTools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        return definitions
            .Select(bucket => bucket with
            {
                ToolNames = bucket.ToolNames.Where(liveNames.Contains).Distinct(StringComparer.Ordinal).ToArray()
            })
            .Where(bucket => includeDisabled
                             || bucket.ToolNames.Count > 0
                             || bucket.Requires is { Count: > 0 })
            .ToArray();
    }

    private static string? ReadExternalMcpServer(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var markerIndex = description.LastIndexOf(ExternalMcpServerMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var start = markerIndex + ExternalMcpServerMarker.Length;
        var end = description.IndexOf('.', start);
        var server = (end < 0 ? description[start..] : description[start..end]).Trim();
        return server.Length == 0 ? null : server;
    }

    public static string BuildDirectory(IReadOnlyList<ToolBucketDefinition> buckets) =>
        string.Join(Environment.NewLine, buckets.Select(bucket =>
            $"- groupId={bucket.Id}; status={(bucket.ToolNames.Count > 0 ? "enabled" : "disabled")}; "
            + $"name={bucket.Name}; requires={FormatRequirements(bucket.Requires)}; "
            + CompactDirectoryDescription(
                bucket.Description,
                bucket.Id.StartsWith("live-", StringComparison.Ordinal)
                    ? MaximumLiveToolDirectoryDescriptionCharacters
                    : MaximumDirectoryDescriptionCharacters)));

    private static string FormatRequirements(IReadOnlyList<string>? requirements) =>
        requirements is { Count: > 0 }
            ? string.Join(",", requirements.Order(StringComparer.Ordinal))
            : "none";

    private static string CompactDirectoryDescription(string description, int maximumCharacters)
    {
        var normalized = description.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters] + "...";
    }

    private static IReadOnlyList<ToolBucketDefinition> KnownDefinitions() =>
    [
        new(
            "capability-discovery",
            "Capability discovery",
            "Inspect the authoritative registry or semantically open another tool drawer when the loaded schemas do not cover the next atomic step.",
            [AliCapabilityCatalog.SemanticDiscoverToolsName, AliCapabilityCatalog.ListAvailableToolsName],
            AlwaysVisible: true),
        new(
            "participant-memory",
            "Participant identity and durable memory recall",
            "Inspect the selected participant profile, recall relevant durable memory, and list that participant's current memories.",
            [
                AliCapabilityCatalog.GetActiveUserProfileName,
                AliCapabilityCatalog.RecallUserMemoryName,
                AliCapabilityCatalog.ListCurrentUserMemoriesName
            ]),
        new(
            "participant-memory-change",
            "Participant memory proposals and changes",
            "Propose, consent to, and reconcile durable participant-memory mutations through the participant-aware memory boundary.",
            [
                AliCapabilityCatalog.MutateParticipantMemoryName,
                AliCapabilityCatalog.ConsentParticipantMemoryProposalName,
                AliCapabilityCatalog.ReconcileParticipantMemoryMutationName
            ]),
        new(
            "current-information",
            "Current web, local knowledge and authoritative time",
            "Search current web evidence for weather, news, prices, scores, schedules and other changing facts; perform deeper web research, search the local document library, and read authoritative local time.",
            [
                AliCapabilityCatalog.SearchCurrentWebName,
                AliCapabilityCatalog.ResearchWebName,
                AliCapabilityCatalog.SearchLocalLibraryName,
                AliCapabilityCatalog.GetCurrentLocalTimeName
            ]),
        new(
            "everyday-assistance",
            "Directions, calendar and assistant identity",
            "Create a Google Maps directions link, create a calendar event, or inspect Ali's assistant identity.",
            [
                AliCapabilityCatalog.CreateGoogleMapsDirectionsLinkName,
                AliCapabilityCatalog.CreateCalendarEventName,
                AliCapabilityCatalog.GetAssistantIdentityName
            ]),
        new(
            "files",
            "Files, folders and archives",
            "Read, write, edit, delete recoverably, list, search, rename, copy, create directories, inspect metadata and hashes, and create, inspect or extract ZIP, TAR, TAR.GZ, GZip and 7z archives.",
            [
                AliCapabilityCatalog.FileWriteName,
                AliCapabilityCatalog.FileReadName,
                AliCapabilityCatalog.FileDeleteName,
                AliCapabilityCatalog.FileListName,
                AliCapabilityCatalog.FileSearchName,
                AliCapabilityCatalog.FileReplaceName,
                AliCapabilityCatalog.FileReplaceLinesName,
                AliCapabilityCatalog.FileMoveName,
                AliCapabilityCatalog.FileCopyName,
                AliCapabilityCatalog.FileCreateDirectoryName,
                AliCapabilityCatalog.FileMetadataName,
                AliCapabilityCatalog.ArchiveCreateName,
                AliCapabilityCatalog.ArchiveListName,
                AliCapabilityCatalog.ArchiveExtractName
            ]),
        new(
            "work-memory",
            "Private task work memory",
            "Conversation-scoped notes and drafts for long multi-step tasks; separate from personal Mem0 memory and document RAG.",
            [
                AliCapabilityCatalog.WorkMemoryWriteName,
                AliCapabilityCatalog.WorkMemoryReadName,
                AliCapabilityCatalog.WorkMemoryDeleteName,
                AliCapabilityCatalog.WorkMemoryListName,
                AliCapabilityCatalog.WorkMemorySearchName,
                AliCapabilityCatalog.WorkMemoryReplaceName,
                AliCapabilityCatalog.WorkMemoryReplaceLinesName
            ]),
        new(
            "skills-and-mode",
            "Agent Skills and operating mode",
            "Load exact installed Agent Skills and their resources or scripts, and inspect or explicitly change the current framework mode.",
            [
                AliCapabilityCatalog.GetAgentModeName,
                AliCapabilityCatalog.SetAgentModeName,
                AliCapabilityCatalog.LoadAgentSkillName,
                AliCapabilityCatalog.ReadAgentSkillResourceName,
                AliCapabilityCatalog.RunAgentSkillScriptName
            ]),
        new(
            "programming-core",
            "Programming core",
            "Language detection, project inspection, indexing, symbol search, analysis, formatting, build, test, run, architecture, bounded context, HTTP probing and process inspection.",
            [
                AliCapabilityCatalog.CodingListCapabilitiesName,
                AliCapabilityCatalog.CodingInspectProjectName,
                AliCapabilityCatalog.CodingIndexProjectName,
                AliCapabilityCatalog.CodingSearchSymbolsName,
                AliCapabilityCatalog.CodingAnalyzeProjectName,
                AliCapabilityCatalog.CodingFormatProjectName,
                AliCapabilityCatalog.CodingBuildProjectName,
                AliCapabilityCatalog.CodingTestProjectName,
                AliCapabilityCatalog.CodingRunProjectName,
                AliCapabilityCatalog.CodingInspectArchitectureName,
                AliCapabilityCatalog.CodingBuildContextName,
                AliCapabilityCatalog.CodingProbeServiceName,
                AliCapabilityCatalog.CodingInspectProcessName
            ],
            ["files"]),
        new(
            "csharp-dotnet",
            "C# and .NET",
            "Default new-project ecosystem for C# console and WPF work: Roslyn semantics and IntelliSense, MSBuild, dependencies, debugging, testing, verification and delivery.",
            [
                AliCapabilityCatalog.DotNetCreateProjectName,
                AliCapabilityCatalog.RoslynAnalyzeProjectName,
                AliCapabilityCatalog.RoslynFormatProjectName,
                AliCapabilityCatalog.RoslynFindSymbolName,
                AliCapabilityCatalog.RoslynGetCompletionsName,
                AliCapabilityCatalog.RoslynInspectSolutionName,
                AliCapabilityCatalog.RoslynInspectDocumentName,
                AliCapabilityCatalog.RoslynInspectPositionName,
                AliCapabilityCatalog.RoslynFindReferencesName,
                AliCapabilityCatalog.RoslynInspectTargetName,
                AliCapabilityCatalog.RoslynListActionsName,
                AliCapabilityCatalog.RoslynPreviewActionName,
                AliCapabilityCatalog.RoslynApplyActionName,
                AliCapabilityCatalog.RoslynVerifyChangesetName,
                AliCapabilityCatalog.DotNetBuildName,
                AliCapabilityCatalog.DotNetRunName,
                AliCapabilityCatalog.DotNetStopProjectName,
                AliCapabilityCatalog.DotNetTestName,
                AliCapabilityCatalog.DotNetVerifyName,
                AliCapabilityCatalog.DotNetDebugLaunchName,
                AliCapabilityCatalog.DotNetDebugAttachName,
                AliCapabilityCatalog.DotNetDebugInspectName,
                AliCapabilityCatalog.DotNetDebugEvaluateName,
                AliCapabilityCatalog.DotNetDebugBreakpointsName,
                AliCapabilityCatalog.DotNetDebugControlName,
                AliCapabilityCatalog.DotNetDebugStopName,
                AliCapabilityCatalog.DotNetDebugDiagnosticsHandoffName,
                AliCapabilityCatalog.DotNetDependencyInspectName,
                AliCapabilityCatalog.DotNetDependencyPreviewName,
                AliCapabilityCatalog.DotNetDependencyApplyName
            ],
            ["programming-core", "files"]),
        new("python", "Python", "CPython projects using Ruff, basedpyright, pytest, debugpy, coverage and py-spy through the shared language-provider facade.", [], ["programming-core", "files"]),
        new("web", "HTML, CSS, JavaScript and TypeScript", "Browser and Node projects using npm, TypeScript, ESLint, Prettier and vscode-js-debug through the shared language-provider facade.", [], ["programming-core", "files"]),
        new("java", "Java", "JDK, javac, java, jdb, JFR, Gradle and Eclipse JDT projects through the shared language-provider facade.", [], ["programming-core", "files"]),
        new(
            "portable-native",
            "Portable C and C++",
            "Portable C/C++ projects using CMake, Clang, clangd, clang-format, clang-tidy and detected GNU or MSVC compilers.",
            [AliCapabilityCatalog.GnuNativeInspectName, AliCapabilityCatalog.GnuNativeExecuteName],
            ["programming-core", "files"]),
        new(
            "embedded",
            "Embedded systems",
            "Arduino boards, cores, libraries, sketches, compile, upload and IDE operations plus Raspberry Pi libraries, packages, probes and deployment.",
            [
                AliCapabilityCatalog.ArduinoInspectName,
                AliCapabilityCatalog.ArduinoSearchLibrariesName,
                AliCapabilityCatalog.ArduinoInstallCoreName,
                AliCapabilityCatalog.ArduinoInstallLibraryName,
                AliCapabilityCatalog.ArduinoCreateCompileName,
                AliCapabilityCatalog.ArduinoCompileName,
                AliCapabilityCatalog.ArduinoUploadName,
                AliCapabilityCatalog.ArduinoOpenIdeName,
                AliCapabilityCatalog.RaspberryPiLibrariesName,
                AliCapabilityCatalog.RaspberryPiProbeName,
                AliCapabilityCatalog.RaspberryPiInspectLibrariesName,
                AliCapabilityCatalog.RaspberryPiSearchPackagesName,
                AliCapabilityCatalog.RaspberryPiDeployName
            ],
            ["files"]),
        new(
            "devops-quality",
            "DevOps, architecture and quality control",
            "Git, dependency boundaries, quality scans, performance, traces, application verification, release publishing and delivery evidence.",
            [
                AliCapabilityCatalog.GitStatusName,
                AliCapabilityCatalog.GitDiffName,
                AliCapabilityCatalog.GitHistoryName,
                AliCapabilityCatalog.GitBlameName,
                AliCapabilityCatalog.GitCreateBranchName,
                AliCapabilityCatalog.GitCommitName,
                AliCapabilityCatalog.GitPushName,
                AliCapabilityCatalog.ArchitectureInspectName,
                AliCapabilityCatalog.ArchitectureCheckName,
                AliCapabilityCatalog.DotNetQualityScanName,
                AliCapabilityCatalog.DotNetPerformanceMeasureName,
                AliCapabilityCatalog.DotNetPerformanceCompareName,
                AliCapabilityCatalog.DotNetPerformanceTraceName,
                AliCapabilityCatalog.DotNetApplicationVerifyName,
                AliCapabilityCatalog.DotNetReleasePublishName,
                AliCapabilityCatalog.DotNetArchitectureReportName,
                AliCapabilityCatalog.DotNetDeliveryVerifyName
            ],
            ["programming-core"]),
        new(
            "visual-studio",
            "Visual Studio",
            "Cold IDE drawer for installed Visual Studio workloads, IDE launch and Visual Studio MSBuild; opened only when the task benefits from the IDE.",
            [AliCapabilityCatalog.VisualStudioInspectName, AliCapabilityCatalog.VisualStudioBuildName, AliCapabilityCatalog.VisualStudioOpenName],
            ["files"]),
        new("aspnet", "ASP.NET", "Cold web specialty for an existing or explicitly requested ASP.NET project; combines the .NET and web drawers.", [], ["csharp-dotnet", "web"]),
        new("visual-cpp", "Visual C++", "Cold Windows-native specialty for an existing or explicitly requested MSVC or Visual Studio C++ project.", [], ["portable-native", "visual-studio"]),
        new(
            "specialists-workflows",
            "Specialists and checkpointed workflows",
            "Private software, research and office advisers; artifact and programming workflows; Magentic orchestration; and explicit recovery of interrupted work.",
            [
                AliCapabilityCatalog.ConsultSoftwareEngineerName,
                AliCapabilityCatalog.ConsultResearcherName,
                AliCapabilityCatalog.ConsultOfficeSpecialistName,
                AliCapabilityCatalog.RunResearchArtifactWorkflowName,
                AliCapabilityCatalog.RunProgrammingGroupChatName,
                AliCapabilityCatalog.RunMagenticOrchestrationName,
                AliCapabilityCatalog.ListRecoverableWorkflowsName,
                AliCapabilityCatalog.ResumeWorkflowCheckpointName
            ]),
        new(
            "external-coding-agents",
            "External coding executor and verification",
            "The user-selected Aider or OpenHands implementation bridge. Its Programming Core dependency supplies Ali's compact language-aware inspection, build, test and run evidence tools in the same turn.",
            [AliCapabilityCatalog.CodingAgentStatusName, AliCapabilityCatalog.CodingAgentExecuteName],
            ["programming-core"])
    ];
}
