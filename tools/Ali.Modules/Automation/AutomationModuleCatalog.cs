using Ali.Modules;

namespace Ali.Modules.Automation;

public sealed record AliAutomationModuleDescriptor(
    string Id,
    string SourceModuleId,
    string DisplayName,
    string Purpose);

public static class AliAutomationModuleCatalog
{
    public static IReadOnlyList<AliAutomationModuleDescriptor> Default { get; } =
        AliModuleCatalog.Default
            .Select(module => new AliAutomationModuleDescriptor(
                ToAutomationModuleId(module.Id),
                module.Id,
                module.DisplayName,
                $"External automation tasks for the {module.DisplayName} module."))
            .ToArray();

    public static string ToAutomationModuleId(string moduleId)
    {
        const string aliPrefix = "ali.";
        var suffix = moduleId.StartsWith(aliPrefix, StringComparison.OrdinalIgnoreCase)
            ? moduleId[aliPrefix.Length..]
            : moduleId;

        return $"ali.automation.{suffix}";
    }
}
