using System.Text.Json;
using Ali.Modules.UserMemory;
using AvatarBuilder.Modules.Vision.Identity;

namespace Ali.Modules.Identity;

/// <summary>
/// Camera-independent user-profile session. Registered vision identities are profile
/// candidates, but selection is explicit and is not treated as authentication.
/// </summary>
public sealed class ActiveUserSession : IActiveUserSession
{
    private const string JohnDoeName = "John Doe";
    private readonly object _sync = new();
    private readonly string _statePath;
    private readonly IPersonIdentityReviewService _identityProfiles;
    private List<ActiveUser> _available = [];
    private ActiveUser _current = null!;
    private bool _requiresSelection;
    private long _selectionRevision;

    public ActiveUserSession(string settingsRoot, string identityDataRoot)
        : this(settingsRoot, new StoredPersonIdentityReviewService(identityDataRoot))
    {
    }

    internal ActiveUserSession(string settingsRoot, IPersonIdentityReviewService identityProfiles)
    {
        _statePath = Path.Combine(settingsRoot, "active-user-session.json");
        _identityProfiles = identityProfiles;
        Refresh();
    }

    public ActiveUser Current { get { lock (_sync) return _current; } }

    public IReadOnlyList<ActiveUser> AvailableUsers { get { lock (_sync) return _available.ToArray(); } }

    public bool RequiresSelection { get { lock (_sync) return _requiresSelection; } }

    public ActiveUserSelectionSnapshot CaptureSelectionSnapshot()
    {
        lock (_sync)
        {
            return _requiresSelection
                ? ActiveUserSelectionSnapshot.SelectionRequired
                : ActiveUserSelectionSnapshot.Resolved(_current);
        }
    }

    public string CaptureSelectionRevision()
    {
        lock (_sync)
        {
            return $"active-user-selection-v1:{_selectionRevision}";
        }
    }

    public event EventHandler<ActiveUser>? Changed;

    public void Refresh()
    {
        ActiveUser? changed = null;
        lock (_sync)
        {
            var previousId = _current?.StableId;
            var previousRequiresSelection = _current is not null && _requiresSelection;
            var configured = _identityProfiles.GetIdentityReviewItems()
                .Where(item => item.IsRegisteredUser && !string.IsNullOrWhiteSpace(item.IdentityId))
                .Select(item => new ActiveUser(
                    item.IdentityId,
                    item.DisplayName,
                    false,
                    "identity-profile-selection",
                    item.Address,
                    item.Email,
                    item.PhoneNumber).Normalize())
                .DistinctBy(item => item.StableId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var persisted = LoadState();
            if (configured.Count == 0)
            {
                var testId = persisted?.TestProfileId;
                if (string.IsNullOrWhiteSpace(testId))
                {
                    testId = $"test_{Guid.NewGuid():N}";
                }
                configured.Add(new ActiveUser(testId, JohnDoeName, true, "identity-test-profile").Normalize());
            }

            _available = configured;
            var persistedSelection = configured.FirstOrDefault(item =>
                item.StableId.Equals(persisted?.SelectedStableId, StringComparison.OrdinalIgnoreCase));
            _requiresSelection = configured.Count > 1 && persistedSelection is null;
            var selected = persistedSelection ?? configured[0];
            if (_requiresSelection)
            {
                selected = selected with { ResolutionMethod = "identity-profile-selection-required" };
            }
            _current = selected;
            if (!_requiresSelection)
            {
                SaveState(selected);
            }
            if (!string.Equals(previousId, selected.StableId, StringComparison.OrdinalIgnoreCase)
                || previousRequiresSelection != _requiresSelection)
            {
                _selectionRevision++;
                changed = selected;
            }
        }

        if (changed is not null)
        {
            Changed?.Invoke(this, changed);
        }
    }

    public ActiveUser Select(string stableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        ActiveUser selected;
        lock (_sync)
        {
            selected = _available.FirstOrDefault(item => item.StableId.Equals(stableId.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Select a configured identity profile before using personal memory.");
            if (_current.StableId.Equals(selected.StableId, StringComparison.OrdinalIgnoreCase))
            {
                if (!_requiresSelection) return _current;
            }
            _requiresSelection = false;
            _current = selected;
            SaveState(selected);
            _selectionRevision++;
        }
        Changed?.Invoke(this, selected);
        return selected;
    }

    private SessionState? LoadState()
    {
        try
        {
            return File.Exists(_statePath)
                ? JsonSerializer.Deserialize<SessionState>(File.ReadAllText(_statePath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void SaveState(ActiveUser user)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var state = new SessionState(user.StableId, user.IsTestProfile ? user.StableId : null);
        File.WriteAllText(_statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record SessionState(string SelectedStableId, string? TestProfileId);
}
