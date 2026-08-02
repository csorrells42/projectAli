namespace Ali.UI.ViewModels;

public sealed class AgentOrchestrationSettingsViewModel
{
    public AgentOrchestrationSettingsViewModel(AliServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
    }

    public string StatusText { get; } =
        "Ali is the only user-facing agent and uses one Agent Framework execution loop. No secondary model loop is enabled.";
}
