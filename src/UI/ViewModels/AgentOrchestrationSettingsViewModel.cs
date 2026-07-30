using System.Windows.Input;
using Ali.Modules.Coordinator;

namespace Ali.UI.ViewModels;

public sealed class AgentOrchestrationSettingsViewModel : ObservableObject
{
    private readonly AliServices _services;
    private MagenticPolicyChoice _selectedMagenticPolicy = MagenticPolicyChoice.AskFirst;
    private int _magenticMaximumRounds = 6;
    private ProgrammingAgentModeChoice _selectedProgrammingAgentMode = ProgrammingAgentModeChoice.Hybrid;
    private bool _alwaysUseProgrammingAgent;
    private string _openHandsWslDistribution = "Ubuntu-24.04";
    private string _aiderStatusText = "Aider readiness has not been checked yet.";
    private string _openHandsStatusText = "OpenHands readiness has not been checked yet.";
    private string _statusText = "Agent orchestration settings have not been loaded yet.";
    private string _checkpointSummary = "Checking workflow checkpoints...";

    public AgentOrchestrationSettingsViewModel(AliServices services)
    {
        _services = services;
        SaveCommand = new RelayCommand(_ => Save(), onException: HandleError);
        ReloadCommand = new RelayCommand(_ => Reload(), onException: HandleError);
        ArchiveCheckpointsCommand = new RelayCommand(_ => ArchiveCheckpoints(), onException: HandleError);
        RefreshProgrammingAgentsCommand = new RelayCommand(_ => _ = RefreshProgrammingAgentsAsync(), onException: HandleError);
        Reload();
    }

    public IReadOnlyList<MagenticPolicyChoice> MagenticPolicyChoices { get; } =
    [
        MagenticPolicyChoice.Off,
        MagenticPolicyChoice.AskFirst,
        MagenticPolicyChoice.Automatic
    ];

    public IReadOnlyList<int> MaximumRoundChoices { get; } = Enumerable.Range(2, 11).ToArray();

    public IReadOnlyList<ProgrammingAgentModeChoice> ProgrammingAgentModeChoices { get; } =
    [
        ProgrammingAgentModeChoice.Aider,
        ProgrammingAgentModeChoice.OpenHands,
        ProgrammingAgentModeChoice.Hybrid
    ];

    public MagenticPolicyChoice SelectedMagenticPolicy
    {
        get => _selectedMagenticPolicy;
        set => SetProperty(ref _selectedMagenticPolicy, value ?? MagenticPolicyChoice.AskFirst);
    }

    public int MagenticMaximumRounds
    {
        get => _magenticMaximumRounds;
        set => SetProperty(ref _magenticMaximumRounds, Math.Clamp(value, 2, 12));
    }

    public ProgrammingAgentModeChoice SelectedProgrammingAgentMode
    {
        get => _selectedProgrammingAgentMode;
        set => SetProperty(ref _selectedProgrammingAgentMode, value ?? ProgrammingAgentModeChoice.Hybrid);
    }

    public bool AlwaysUseProgrammingAgent
    {
        get => _alwaysUseProgrammingAgent;
        set => SetProperty(ref _alwaysUseProgrammingAgent, value);
    }

    public string OpenHandsWslDistribution
    {
        get => _openHandsWslDistribution;
        set => SetProperty(ref _openHandsWslDistribution, value);
    }

    public string AiderStatusText
    {
        get => _aiderStatusText;
        private set => SetProperty(ref _aiderStatusText, value);
    }

    public string OpenHandsStatusText
    {
        get => _openHandsStatusText;
        private set => SetProperty(ref _openHandsStatusText, value);
    }

    public string SettingsPath => _services.AgentOrchestrationSettingsPath;

    public string CheckpointPath => _services.WorkflowCheckpointPath;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CheckpointSummary
    {
        get => _checkpointSummary;
        private set => SetProperty(ref _checkpointSummary, value);
    }

    public ICommand SaveCommand { get; }

    public ICommand ReloadCommand { get; }

    public ICommand ArchiveCheckpointsCommand { get; }

    public ICommand RefreshProgrammingAgentsCommand { get; }

    public void Reload()
    {
        var settings = _services.LoadAgentOrchestrationSettings();
        SelectedMagenticPolicy = MagenticPolicyChoices.First(choice =>
            choice.Value == settings.MagenticPolicy);
        MagenticMaximumRounds = settings.MagenticMaximumRounds;
        SelectedProgrammingAgentMode = ProgrammingAgentModeChoices.First(choice =>
            choice.Value == settings.ProgrammingAgentMode);
        AlwaysUseProgrammingAgent = settings.AlwaysUseProgrammingAgent;
        OpenHandsWslDistribution = settings.OpenHandsWslDistribution;
        RefreshCheckpointSummary();
        _ = RefreshProgrammingAgentsAsync();
        StatusText = "Loaded Agent Framework orchestration policy. Changes apply on Ali's next turn.";
    }

    private void Save()
    {
        _services.SaveAgentOrchestrationSettings(new AgentOrchestrationSettings
        {
            MagenticPolicy = SelectedMagenticPolicy.Value,
            MagenticMaximumRounds = MagenticMaximumRounds,
            ProgrammingAgentMode = SelectedProgrammingAgentMode.Value,
            AlwaysUseProgrammingAgent = AlwaysUseProgrammingAgent,
            OpenHandsWslDistribution = OpenHandsWslDistribution
        });
        RefreshCheckpointSummary();
        _ = RefreshProgrammingAgentsAsync();
        StatusText = $"Saved Magentic policy: {SelectedMagenticPolicy.DisplayName}; programming engine: {SelectedProgrammingAgentMode.DisplayName}; required for programming work: {(AlwaysUseProgrammingAgent ? "yes" : "no")}. Changes apply on Ali's next turn.";
    }

    private void ArchiveCheckpoints()
    {
        if (!Directory.Exists(CheckpointPath)
            || !Directory.EnumerateFileSystemEntries(CheckpointPath).Any())
        {
            Directory.CreateDirectory(CheckpointPath);
            RefreshCheckpointSummary();
            StatusText = "There were no workflow checkpoints to archive.";
            return;
        }

        var parent = Directory.GetParent(CheckpointPath)?.FullName
            ?? throw new InvalidOperationException("The workflow checkpoint folder has no parent.");
        var archiveRoot = Path.Combine(parent, "WorkflowCheckpointArchive");
        Directory.CreateDirectory(archiveRoot);
        var destination = Path.Combine(archiveRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.Move(CheckpointPath, destination);
        Directory.CreateDirectory(CheckpointPath);
        RefreshCheckpointSummary();
        StatusText = $"Archived recoverable workflow checkpoints to {destination}.";
    }

    private void RefreshCheckpointSummary()
    {
        Directory.CreateDirectory(CheckpointPath);
        var files = Directory.EnumerateFiles(CheckpointPath, "*", SearchOption.AllDirectories).Count();
        CheckpointSummary = files == 0
            ? "Durable workflow checkpointing is on; no checkpoint files exist yet."
            : $"Durable workflow checkpointing is on; {files} checkpoint file(s) are stored locally.";
    }

    private void HandleError(Exception ex) =>
        StatusText = $"Agent orchestration settings failed safely: {ex.Message.ReplaceLineEndings(" ").Trim()}";

    private async Task RefreshProgrammingAgentsAsync()
    {
        try
        {
            AiderStatusText = "Checking Aider...";
            OpenHandsStatusText = "Checking OpenHands...";
            var status = await _services.CodingModule.ExternalAgents.GetStatusAsync(CancellationToken.None);
            AiderStatusText = $"{(status.Aider.Ready ? "Ready" : "Unavailable")}: {status.Aider.Summary}";
            OpenHandsStatusText = $"{(status.OpenHands.Ready ? "Ready" : "Unavailable")}: {status.OpenHands.Summary}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AiderStatusText = "Aider readiness check failed safely.";
            OpenHandsStatusText = $"Provider readiness check failed safely: {ex.Message.ReplaceLineEndings(" ").Trim()}";
        }
    }
}

public sealed record ProgrammingAgentModeChoice(string Value, string DisplayName, string Summary)
{
    public static ProgrammingAgentModeChoice Aider { get; } = new(
        ProgrammingAgentModes.Aider,
        "Aider",
        "Architect and refinement first. Best when design quality, repo-map context, and precise edits matter most.");

    public static ProgrammingAgentModeChoice OpenHands { get; } = new(
        ProgrammingAgentModes.OpenHands,
        "OpenHands",
        "Autonomous implementation first. Best for grinding through complete multi-step coding work.");

    public static ProgrammingAgentModeChoice Hybrid { get; } = new(
        ProgrammingAgentModes.Hybrid,
        "Hybrid",
        "OpenHands implements, Aider reviews and refines, then Ali checks direct evidence before claiming completion.");
}

public sealed record MagenticPolicyChoice(string Value, string DisplayName, string Summary)
{
    public static MagenticPolicyChoice Off { get; } = new(
        MagenticPolicies.Off,
        "Off",
        "Magentic is removed from Ali's model-callable tools.");

    public static MagenticPolicyChoice AskFirst { get; } = new(
        MagenticPolicies.AskFirst,
        "Ask first",
        "Ali must show the normal permission window before Magentic starts.");

    public static MagenticPolicyChoice Automatic { get; } = new(
        MagenticPolicies.Automatic,
        "Automatic for complex work",
        "The model may select Magentic only for eligible open-ended multi-domain work.");
}
