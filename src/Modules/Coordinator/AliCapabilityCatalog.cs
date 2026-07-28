using System.Text;
using Ali.Modules.Mcp;
using Microsoft.Agents.AI;

namespace Ali.Modules.Coordinator;

public static class AliCapabilityCatalog
{
    public const string ListAvailableToolsName = "list_available_tools";
    public const string SearchMemoryName = "search_memory";
    public const string RememberFactName = "remember_fact";
    public const string RecallUserMemoryName = "recall_user_memory";
    public const string RememberCurrentUserName = "remember_for_current_user";
    public const string CorrectCurrentUserMemoryName = "correct_current_user_memory";
    public const string ForgetCurrentUserMemoryName = "forget_current_user_memory";
    public const string ListCurrentUserMemoriesName = "list_current_user_memories";
    public const string SearchCurrentWebName = "search_current_web";
    public const string ResearchWebName = "research_web";
    public const string SearchLocalLibraryName = "search_local_library";
    public const string CreateReminderName = "create_reminder";
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
    public const string WorkMemoryWriteName = "file_memory_write";
    public const string WorkMemoryReadName = "file_memory_read";
    public const string WorkMemoryDeleteName = "file_memory_delete";
    public const string WorkMemoryListName = "file_memory_ls";
    public const string WorkMemorySearchName = "file_memory_grep";
    public const string WorkMemoryReplaceName = "file_memory_replace";
    public const string WorkMemoryReplaceLinesName = "file_memory_replace_lines";
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
    public const string DotNetBuildName = "dotnet_build_project";
    public const string DotNetRunName = "dotnet_run_project";

    public static IReadOnlyList<CoordinatorCapability> Tools { get; } =
    [
        new(ListAvailableToolsName, "Return Ali's exact currently registered model-callable tool catalog."),
        new(SearchMemoryName, "Search Ali's saved local memories for personal facts, preferences, prior instructions, relationships, and remembered details."),
        new(RememberFactName, "Save a fact in Ali's local memory after an explicit user request."),
        new(RecallUserMemoryName, "Recall relevant long-term memories for the active identity profile. The active user is resolved internally."),
        new(RememberCurrentUserName, "Teach Ali a durable fact for the active identity profile after an explicit user request."),
        new(CorrectCurrentUserMemoryName, "Correct a durable memory belonging to the active identity profile."),
        new(ForgetCurrentUserMemoryName, "Forget matching memories belonging to the active identity profile after confirmation."),
        new(ListCurrentUserMemoriesName, "List memories belonging only to the active identity profile after confirmation."),
        new(SearchCurrentWebName, "Search the configured live internet backends for current or source-dependent information."),
        new(ResearchWebName, "Run provider-managed, multi-source web research for complex nested or comparative questions."),
        new(SearchLocalLibraryName, "Search the user's indexed local RAG library and reference documents."),
        new(CreateReminderName, "Create a local reminder after an explicit user request."),
        new(GetAssistantIdentityName, "Return Ali's configured local assistant identity."),
        new(GetCurrentLocalTimeName, "Return the authoritative local computer date, time, and time zone."),
        new(FileWriteName, "Create a new text file or, after approval, overwrite an existing file in Ali's approved workstation folders.", "Microsoft Agent Framework file access"),
        new(FileReadName, "Read a text file from Ali's approved workstation folders.", "Microsoft Agent Framework file access"),
        new(FileDeleteName, "Move a file from an approved workstation folder into Ali's recoverable trash after approval.", "Microsoft Agent Framework file access"),
        new(FileListName, "List files and folders under Ali's approved workstation roots.", "Microsoft Agent Framework file access"),
        new(FileSearchName, "Search text inside files under Ali's approved workstation roots.", "Microsoft Agent Framework file access"),
        new(FileReplaceName, "Edit matching text in an existing file after approval.", "Microsoft Agent Framework file access"),
        new(FileReplaceLinesName, "Edit specific lines in an existing file after approval.", "Microsoft Agent Framework file access"),
        new(FileMoveName, "Rename or move an existing file between approved workstation folders after approval.", "Ali workstation file tools"),
        new(WorkMemoryWriteName, "Write a private working note or draft for the active user and conversation, optionally with a discovery description.", "Microsoft Agent Framework file memory"),
        new(WorkMemoryReadName, "Read a private working note from the active user and conversation.", "Microsoft Agent Framework file memory"),
        new(WorkMemoryDeleteName, "Move a private working note into Ali's recoverable work-memory trash.", "Microsoft Agent Framework file memory"),
        new(WorkMemoryListName, "List private working notes and their descriptions for the active user and conversation.", "Microsoft Agent Framework file memory"),
        new(WorkMemorySearchName, "Search private working notes for the active user and conversation.", "Microsoft Agent Framework file memory"),
        new(WorkMemoryReplaceName, "Replace matching text in a private working note.", "Microsoft Agent Framework file memory"),
        new(WorkMemoryReplaceLinesName, "Edit specific lines in a private working note.", "Microsoft Agent Framework file memory"),
        new(DotNetCreateProjectName, "Create a new WPF or console C# project in an empty approved folder after user approval.", "Ali .NET coding tools"),
        new(RoslynAnalyzeProjectName, "Load a C# project with Roslyn and return semantic compiler diagnostics.", "Microsoft Roslyn coding intelligence"),
        new(RoslynFormatProjectName, "Format every C# document in an approved project with Roslyn after user approval.", "Microsoft Roslyn coding intelligence"),
        new(RoslynFindSymbolName, "Find C# type and member declarations semantically with Roslyn.", "Microsoft Roslyn coding intelligence"),
        new(RoslynGetCompletionsName, "Return Roslyn IntelliSense completion candidates at a C# source location.", "Microsoft Roslyn coding intelligence"),
        new(RoslynInspectSolutionName, "Inspect a C# project or solution graph with Roslyn.", "Microsoft Roslyn workspace intelligence"),
        new(RoslynInspectDocumentName, "Return Roslyn outline, live diagnostics, and semantic classifications for a C# document.", "Microsoft Roslyn editor intelligence"),
        new(RoslynInspectPositionName, "Return Roslyn hover, definition, and signature information at a C# source position.", "Microsoft Roslyn editor intelligence"),
        new(RoslynFindReferencesName, "Find all semantic references to the C# symbol at a source position.", "Microsoft Roslyn solution intelligence"),
        new(RoslynPreviewRenameName, "Preview a Roslyn solution-wide semantic rename without changing files.", "Microsoft Roslyn refactoring"),
        new(RoslynApplyRenameName, "Apply a Roslyn solution-wide semantic rename after user approval.", "Microsoft Roslyn refactoring"),
        new(DotNetBuildName, "Restore and compile an approved C# .csproj through Microsoft's MSBuild API after user approval.", "Microsoft Roslyn/MSBuild coding tools"),
        new(DotNetRunName, "Launch a successfully built .NET application from an approved project folder after user approval.", "Microsoft Roslyn/MSBuild coding tools")
    ];

    public static CoordinatorCapabilityResult ListAvailableTools() =>
        ListAvailableTools(additionalTools: []);

    public static CoordinatorCapabilityResult ListAvailableTools(McpClientManager mcpClients)
    {
        ArgumentNullException.ThrowIfNull(mcpClients);
        var settings = mcpClients.LoadSettings();
        var additionalTools = settings.Enabled
            ? settings.Servers
                .Where(server => server.Enabled)
                .SelectMany(server => server.Tools
                    .Where(tool => tool.Enabled)
                    .Select(tool => new CoordinatorCapability(
                        McpClientManager.BuildModelToolName(server, tool.Name),
                        string.IsNullOrWhiteSpace(tool.Description)
                            ? $"Run {tool.Name} through the {server.Name} MCP integration."
                            : $"{tool.Description} (MCP server: {server.Name})",
                        $"External MCP: {server.Name}")))
                .ToList()
            : [];
        return ListAvailableTools(additionalTools);
    }

    private static CoordinatorCapabilityResult ListAvailableTools(
        IReadOnlyList<CoordinatorCapability> additionalTools)
    {
        var allTools = Tools.Concat(additionalTools).ToList();
        return
        new(
            $"Ali has {allTools.Count} configured model-callable tools. This complete structured inventory is authoritative. Preserve every returned row and its Source when the user requests the full inventory; filtering and alternate formatting are allowed only when the user asks for them. MCP connection warnings reported in Ali Activity remain authoritative for current availability.",
            allTools);
    }

    public static string BuildPromptManifest()
    {
        var manifest = new StringBuilder()
            .AppendLine("REGISTERED MODEL-CALLABLE TOOLS (authoritative; these are the only tools you may claim to have):");
        foreach (var tool in Tools)
        {
            manifest.Append("- ")
                .Append(tool.Name)
                .Append(": ")
                .AppendLine(tool.Description);
        }

        manifest.Append("Additional tools whose names begin with mcp_ are external MCP integrations explicitly enabled by the user. "
            + "Use list_available_tools for their configured catalog and obey approval requests. "
            + "Voice playback is an application output setting, not a model-callable tool. "
            + "Never claim calendar, email, arbitrary file-system, shell, camera, or generic browser-control access unless an enabled tool with that exact capability appears in the current turn.");
        return manifest.ToString();
    }
}
