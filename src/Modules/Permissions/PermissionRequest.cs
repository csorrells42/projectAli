namespace Ali.Modules.Permissions;

public sealed record PermissionRequest(
    string Id,
    string Capability,
    PermissionRisk Risk,
    string Summary,
    bool UserConfirmed = false)
{
    public static PermissionRequest Create(
        string capability,
        PermissionRisk risk,
        string summary,
        bool userConfirmed = false) =>
        new($"perm_{Guid.NewGuid():N}", capability, risk, summary, userConfirmed);
}
