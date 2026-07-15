namespace Ali.Modules.Permissions;

public enum PermissionDecisionKind
{
    Allow,
    RequireConfirmation,
    Deny
}

public sealed record PermissionDecision(
    PermissionDecisionKind Kind,
    string Reason)
{
    public static PermissionDecision Allow(string reason) =>
        new(PermissionDecisionKind.Allow, reason);

    public static PermissionDecision RequireConfirmation(string reason) =>
        new(PermissionDecisionKind.RequireConfirmation, reason);

    public static PermissionDecision Deny(string reason) =>
        new(PermissionDecisionKind.Deny, reason);
}
