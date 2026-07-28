using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Ali.Modules.Permissions;
using Ali.Modules.UserMemory;
using WpfMessageBox = System.Windows.MessageBox;

namespace Ali.UI.ViewModels;

public sealed class AgentToolPermissionsViewModel : ObservableObject
{
    private readonly AgentToolPermissionStore _store;
    private readonly IActiveUserSession _activeUsers;
    private AgentToolPermissionGrantViewModel? _selectedGrant;
    private AgentPermissionProfileChoiceViewModel? _selectedPermissionProfile;
    private string _statusText = "Saved Agent Framework permissions have not been reviewed yet.";

    public AgentToolPermissionsViewModel(
        AgentToolPermissionStore store,
        IActiveUserSession activeUsers)
    {
        _store = store;
        _activeUsers = activeUsers;
        PermissionProfileChoices =
        [
            new(AgentPermissionProfile.TrustedWorkstation, "Trusted Workstation"),
            new(AgentPermissionProfile.LockedDown, "Locked Down")
        ];
        ReloadCommand = new RelayCommand(_ => Reload());
        RevokeSelectedCommand = new RelayCommand(
            _ => RevokeSelected(),
            _ => SelectedGrant is not null);
        RevokeAllCommand = new RelayCommand(
            _ => RevokeAll(),
            _ => Grants.Count > 0);
        _activeUsers.Changed += ActiveUsersOnChanged;
        Reload();
    }

    public ObservableCollection<AgentToolPermissionGrantViewModel> Grants { get; } = [];

    public ObservableCollection<AgentToolPermissionPolicyViewModel> ProtectedTools { get; } = [];

    public IReadOnlyList<AgentPermissionProfileChoiceViewModel> PermissionProfileChoices { get; }

    public AgentPermissionProfileChoiceViewModel? SelectedPermissionProfile
    {
        get => _selectedPermissionProfile;
        set
        {
            if (value is null || !SetProperty(ref _selectedPermissionProfile, value))
            {
                return;
            }

            _store.SetProfile(value.Profile);
            OnPropertyChanged(nameof(ProfileSummary));
            Reload();
        }
    }

    public string ProfileSummary => _store.CurrentProfile == AgentPermissionProfile.TrustedWorkstation
        ? "Default: local reads and safe registered tools stay fast; destructive, private-write, metered, and externally consequential actions ask first."
        : "Tighter deployment mode: local document, memory, and web reads also require approval.";

    public AgentToolPermissionGrantViewModel? SelectedGrant
    {
        get => _selectedGrant;
        set
        {
            if (SetProperty(ref _selectedGrant, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string CurrentUserText => _activeUsers.RequiresSelection
        ? "Select the active user profile before saved permissions can be used."
        : $"Permissions for {_activeUsers.Current.DisplayName} ({_activeUsers.Current.StableId})";

    public string SettingsPath => _store.SettingsPath;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public ICommand ReloadCommand { get; }

    public ICommand RevokeSelectedCommand { get; }

    public ICommand RevokeAllCommand { get; }

    public void Reload()
    {
        var profileChoice = PermissionProfileChoices.First(choice => choice.Profile == _store.CurrentProfile);
        if (!ReferenceEquals(_selectedPermissionProfile, profileChoice))
        {
            _selectedPermissionProfile = profileChoice;
            OnPropertyChanged(nameof(SelectedPermissionProfile));
        }
        OnPropertyChanged(nameof(ProfileSummary));
        Grants.Clear();
        ProtectedTools.Clear();
        if (!_activeUsers.RequiresSelection)
        {
            foreach (var grant in _store.ListForUser(_activeUsers.Current.StableId))
            {
                Grants.Add(new AgentToolPermissionGrantViewModel(grant));
            }
        }

        foreach (var policy in AliToolPermissionPolicy.ProtectedToolsFor(_store.CurrentProfile))
        {
            var matchingGrants = Grants
                .Where(grant => grant.RawToolName.Equals(policy.ToolName, StringComparison.Ordinal))
                .ToArray();
            ProtectedTools.Add(new AgentToolPermissionPolicyViewModel(
                policy,
                matchingGrants,
                ToggleToolPermission,
                !_activeUsers.RequiresSelection));
        }

        SelectedGrant = Grants.FirstOrDefault();
        OnPropertyChanged(nameof(CurrentUserText));
        StatusText = _activeUsers.RequiresSelection
            ? "Saved rules are fail-closed until an active user is selected."
            : Grants.Count == 0
                ? "No standing permissions are saved. Approval-required tools will ask first."
                : $"Loaded {Grants.Count} revocable standing permission rule(s).";
        RaiseCommandStates();
    }

    private void RevokeSelected()
    {
        if (SelectedGrant is null || _activeUsers.RequiresSelection)
        {
            return;
        }

        if (WpfMessageBox.Show(
                $"Revoke the saved permission for {SelectedGrant.ToolName}? Ali will ask again the next time it is needed.",
                "Revoke permission",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var removed = _store.Revoke(_activeUsers.Current.StableId, SelectedGrant.Id);
        Reload();
        StatusText = removed
            ? "Saved permission revoked. Ali will ask before the next matching call."
            : "That permission was already absent.";
    }

    private void RevokeAll()
    {
        if (_activeUsers.RequiresSelection || Grants.Count == 0)
        {
            return;
        }

        if (WpfMessageBox.Show(
                $"Revoke all saved Agent Framework permissions for {_activeUsers.Current.DisplayName}?",
                "Revoke all permissions",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var removed = _store.RevokeAll(_activeUsers.Current.StableId);
        Reload();
        StatusText = $"Revoked {removed} saved permission rule(s).";
    }

    private void ToggleToolPermission(AgentToolPermissionPolicyViewModel policy)
    {
        if (_activeUsers.RequiresSelection)
        {
            return;
        }

        if (policy.IsAlwaysAllowed)
        {
            if (WpfMessageBox.Show(
                    $"Require approval before Ali uses {policy.ToolName}? Exact-call approvals, if any, will remain saved.",
                    "Require approval",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(policy.ToolGrantId))
            {
                _store.Revoke(_activeUsers.Current.StableId, policy.ToolGrantId);
            }

            Reload();
            StatusText = $"{policy.ToolName} will ask before use unless an exact saved call matches.";
            return;
        }

        if (WpfMessageBox.Show(
                $"Always allow Ali to use {policy.ToolName} for {_activeUsers.Current.DisplayName} without asking first?",
                "Always allow tool",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        _store.Save(
            _activeUsers.Current,
            policy.RawToolName,
            AgentToolPermissionScope.Tool,
            arguments: null);
        Reload();
        StatusText = $"{policy.ToolName} is allowed for {_activeUsers.Current.DisplayName} until revoked.";
    }

    private void ActiveUsersOnChanged(object? sender, ActiveUser user) => Reload();

    private void RaiseCommandStates()
    {
        (RevokeSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RevokeAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}

public sealed record AgentPermissionProfileChoiceViewModel(
    AgentPermissionProfile Profile,
    string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class AgentToolPermissionGrantViewModel(AgentToolPermissionGrant grant)
{
    public string Id => grant.Id;

    internal string RawToolName => grant.ToolName;

    public string ToolName => grant.ToolName.Replace('_', ' ');

    public string ScopeText => grant.Scope == AgentToolPermissionScope.Tool
        ? "Always allow this tool"
        : "Allow only these exact arguments";

    internal AgentToolPermissionScope Scope => grant.Scope;

    public string ArgumentText => grant.ArgumentSummary;

    public string CreatedText => $"Saved {grant.CreatedUtc.ToLocalTime():g}";
}

public sealed class AgentToolPermissionPolicyViewModel
{
    private readonly AgentToolPermissionDefinition _policy;
    private readonly IReadOnlyCollection<AgentToolPermissionGrantViewModel> _matchingGrants;
    private readonly AgentToolPermissionGrantViewModel? _toolGrant;

    public AgentToolPermissionPolicyViewModel(
        AgentToolPermissionDefinition policy,
        IReadOnlyCollection<AgentToolPermissionGrantViewModel> matchingGrants,
        Action<AgentToolPermissionPolicyViewModel> toggle,
        bool canEdit)
    {
        _policy = policy;
        _matchingGrants = matchingGrants;
        _toolGrant = matchingGrants.FirstOrDefault(grant => grant.Scope == AgentToolPermissionScope.Tool);
        EditCommand = new RelayCommand(_ => toggle(this), _ => canEdit);
    }

    public string RawToolName => _policy.ToolName;

    public string ToolName => _policy.ToolName.Replace('_', ' ');

    public string Reason => _policy.Reason;

    public bool IsAlwaysAllowed => _toolGrant is not null;

    public string? ToolGrantId => _toolGrant?.Id;

    public string CurrentBehavior => _matchingGrants.Count == 0
        ? "Ask before use"
        : IsAlwaysAllowed
            ? "Allowed for this user until revoked"
            : $"Ask unless one of {_matchingGrants.Count} exact saved call(s) matches";

    public string EditButtonText => IsAlwaysAllowed
        ? "Require approval"
        : "Always allow tool";

    public ICommand EditCommand { get; }
}
