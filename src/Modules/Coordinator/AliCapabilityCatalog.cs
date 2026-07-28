using System.Text;
using Ali.Modules.Mcp;

namespace Ali.Modules.Coordinator;

public static class AliCapabilityCatalog
{
    public const string ListAvailableToolsName = "list_available_tools";
    public const string SearchMemoryName = "search_memory";
    public const string RememberFactName = "remember_fact";
    public const string SearchCurrentWebName = "search_current_web";
    public const string ResearchWebName = "research_web";
    public const string SearchLocalLibraryName = "search_local_library";
    public const string CreateReminderName = "create_reminder";
    public const string GetAssistantIdentityName = "get_assistant_identity";
    public const string GetCurrentLocalTimeName = "get_current_local_time";

    public static IReadOnlyList<CoordinatorCapability> Tools { get; } =
    [
        new(ListAvailableToolsName, "Return Ali's exact currently registered model-callable tool catalog."),
        new(SearchMemoryName, "Search Ali's saved local memories for personal facts, preferences, prior instructions, relationships, and remembered details."),
        new(RememberFactName, "Save a fact in Ali's local memory after an explicit user request."),
        new(SearchCurrentWebName, "Search the configured live internet backends for current or source-dependent information."),
        new(ResearchWebName, "Run provider-managed, multi-source web research for complex nested or comparative questions."),
        new(SearchLocalLibraryName, "Search the user's indexed local RAG library and reference documents."),
        new(CreateReminderName, "Create a local reminder after an explicit user request."),
        new(GetAssistantIdentityName, "Return Ali's configured local assistant identity."),
        new(GetCurrentLocalTimeName, "Return the authoritative local computer date, time, and time zone.")
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
                            : $"{tool.Description} (MCP server: {server.Name})")))
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
            $"Ali has {allTools.Count} configured model-callable tools. MCP connection warnings reported in Ali Activity remain authoritative for current availability.",
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
