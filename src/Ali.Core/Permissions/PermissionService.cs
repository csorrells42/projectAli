namespace Ali.Core.Permissions;

public sealed class PermissionService
{
    public PermissionDecision Evaluate(PermissionRequest request)
    {
        if (request.Risk == PermissionRisk.ReadOnly)
        {
            return PermissionDecision.Allow("Read-only action is allowed by the current bootstrap policy.");
        }

        if (request.UserConfirmed)
        {
            return PermissionDecision.Allow($"User confirmed {request.Risk} action.");
        }

        return request.Risk switch
        {
            PermissionRisk.AdminSystemAction or PermissionRisk.Destructive =>
                PermissionDecision.RequireConfirmation("High-risk action requires explicit confirmation."),

            PermissionRisk.PackageRestore or PermissionRisk.NetworkEgress or PermissionRisk.ScriptExecution =>
                PermissionDecision.RequireConfirmation("Network, package, and script actions require explicit confirmation."),

            _ => PermissionDecision.RequireConfirmation($"{request.Risk} action requires explicit confirmation.")
        };
    }
}
