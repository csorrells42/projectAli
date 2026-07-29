using System.Windows.Input;
using Ali.Modules.Coordinator;

namespace Ali.UI.ViewModels;

public sealed class AgentOrchestrationSettingsViewModel : ObservableObject
{
    private readonly AliServices _services;
    private MagenticPolicyChoice _selectedMagenticPolicy = MagenticPolicyChoice.AskFirst;
    private int _magenticMaximumRounds = 6;
    private string _statusText = "Agent orchestration settings have not been loaded yet.";
    private string _checkpointSummary = "Checking workflow checkpoints...";

    public AgentOrchestrationSettingsViewModel(AliServices services)
    {
        _services = services;
        SaveCommand = new RelayCommand(_ => Save(), onException: HandleError);
        ReloadCommand = new RelayCommand(_ => Reload(), onException: HandleError);
        ArchiveCheckpointsCommand = new RelayCommand(_ => ArchiveCheckpoints(), onException: HandleError);
        Reload();
    }

    public IReadOnlyList<MagenticPolicyChoice> MagenticPolicyChoices { get; } =
    [
        MagenticPolicyChoice.Off,
        MagenticPolicyChoice.AskFirst,
        MagenticPolicyChoice.Automatic
    ];

    public IReadOnlyList<int> MaximumRoundChoices { get; } = Enumerable.Range(2, 11).ToArray();

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

    public void Reload()
    {
        var settings = _services.LoadAgentOrchestrationSettings();
        SelectedMagenticPolicy = MagenticPolicyChoices.First(choice =>
            choice.Value == settings.MagenticPolicy);
        MagenticMaximumRounds = settings.MagenticMaximumRounds;
        RefreshCheckpointSummary();
        StatusText = "Loaded Agent Framework orchestration policy. Changes apply on Ali's next turn.";
    }

    private void Save()
    {
        _services.SaveAgentOrchestrationSettings(new AgentOrchestrationSettings
        {
            MagenticPolicy = SelectedMagenticPolicy.Value,
            MagenticMaximumRounds = MagenticMaximumRounds
        });
        RefreshCheckpointSummary();
        StatusText = $"Saved Magentic policy: {SelectedMagenticPolicy.DisplayName}. Changes apply on Ali's next turn.";
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
