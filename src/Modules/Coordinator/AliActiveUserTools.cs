using Ali.Modules.UserMemory;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Exposes the explicitly selected local identity as model-callable evidence.
/// It provides data only; the model remains responsible for deciding when the
/// active profile is relevant and what the returned fields mean for the task.
/// </summary>
internal sealed class AliActiveUserTools(
    IActiveUserSession? activeUsers,
    Func<CoordinatorTurnContext?> turnAccessor)
{
    public CoordinatorActiveUserResult GetActiveProfile()
    {
        var turn = turnAccessor();
        var selection = turn?.CapturedUserSelection ?? activeUsers?.CaptureSelectionSnapshot();
        return GetActiveProfile(selection, turn);
    }

    internal CoordinatorActiveUserResult GetActiveProfile(ActiveUserSelectionSnapshot selection) =>
        GetActiveProfile(selection, turn: null);

    private static CoordinatorActiveUserResult GetActiveProfile(
        ActiveUserSelectionSnapshot? selection,
        CoordinatorTurnContext? turn)
    {
        turn?.Report(
            AgentActivityKind.ToolCall,
            "Reading selected user profile",
            "Ali requested the active local identity profile as authoritative data.");

        if (selection is null)
        {
            return new(
                false,
                "The active-user identity service is unavailable.",
                null,
                null,
                null,
                null,
                null);
        }

        if (!selection.IsResolved)
        {
            turn?.Report(
                AgentActivityKind.Warning,
                "Active user profile is not selected",
                "More than one identity profile is available; Ali did not choose one on the user's behalf.");
            return new(
                false,
                "Select the active user profile before using personal identity data.",
                null,
                null,
                null,
                null,
                null);
        }

        var user = selection.SelectedUser!.Normalize();
        if (turn is not null)
        {
            turn.UsedEvidenceTool = true;
            turn.Report(
                AgentActivityKind.ToolResult,
                "Loaded selected user profile",
                $"Loaded {user.DisplayName}'s local identity profile; saved address is "
                + (string.IsNullOrWhiteSpace(user.Address) ? "not available." : "available."));
        }

        return new(
            true,
            "Loaded the explicitly selected local identity profile.",
            user.StableId,
            user.DisplayName,
            user.Address,
            user.Email,
            user.PhoneNumber);
    }
}
