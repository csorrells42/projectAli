using System.Text;

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
        new(
            $"Ali has {Tools.Count} registered model-callable tools. This list is authoritative for the current session.",
            Tools);

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

        manifest.Append("Voice playback is an application output setting, not a model-callable tool. "
            + "You do not currently have direct calendar, email, arbitrary file-system, shell, camera, or generic browser-control tools.");
        return manifest.ToString();
    }
}
