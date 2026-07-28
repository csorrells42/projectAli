using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ali.Modules.Coding.Infrastructure;

namespace Ali.Modules.Coding.Dependencies;

public sealed record PackageReferenceInfo(string Id, string? Version, bool IsCentral, string? License, bool? Vulnerable);
public sealed record DependencyInspectionResult(bool Success, string Summary, IReadOnlyList<PackageReferenceInfo> Packages, bool LockFilePresent, string AuditOutput);
public sealed record DependencyChangeResult(bool Success, string Summary, string ProjectPath, string Action, string PackageId, string? Version, string Preview, bool Applied);

internal sealed partial class AliDependencyEngineering(AliCodingProjectResolver resolver)
{
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdPattern();
    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z.+-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public async Task<DependencyInspectionResult> InspectAsync(string projectPath, CancellationToken cancellationToken)
    {
        var project = resolver.ResolveExistingProject(projectPath);
        var document = XDocument.Load(project.PhysicalPath, LoadOptions.PreserveWhitespace);
        var packages = document.Descendants().Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => new PackageReferenceInfo(
                (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update") ?? "unknown",
                (string?)element.Attribute("Version") ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value,
                false, null, null)).ToList();
        var audit = await AliBoundedProcessRunner.RunAsync(ResolveDotNet(), project.ProjectDirectory,
            ["list", project.PhysicalPath, "package", "--include-transitive", "--vulnerable", "--deprecated", "--format", "json"],
            TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
        return new DependencyInspectionResult(audit.Success,
            audit.Success ? $"Inspected {packages.Count} direct package reference(s) and completed NuGet vulnerability/deprecation audit."
                : "Package references were read, but the NuGet audit did not complete.",
            packages, File.Exists(Path.Combine(project.ProjectDirectory, "packages.lock.json")), audit.Output);
    }

    public Task<DependencyChangeResult> PreviewChangeAsync(string projectPath, string action, string packageId, string? version, CancellationToken cancellationToken) =>
        ChangeAsync(projectPath, action, packageId, version, apply: false, cancellationToken);

    public Task<DependencyChangeResult> ApplyChangeAsync(string projectPath, string action, string packageId, string? version, CancellationToken cancellationToken) =>
        ChangeAsync(projectPath, action, packageId, version, apply: true, cancellationToken);

    private Task<DependencyChangeResult> ChangeAsync(string projectPath, string action, string packageId, string? version, bool apply, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = resolver.ResolveExistingProject(projectPath);
        var normalizedAction = action.Trim().ToLowerInvariant();
        if (normalizedAction is not ("add" or "update" or "remove")) throw new ArgumentException("Action must be add, update, or remove.", nameof(action));
        if (!PackageIdPattern().IsMatch(packageId)) throw new ArgumentException("Package ID is invalid.", nameof(packageId));
        if (normalizedAction != "remove" && (string.IsNullOrWhiteSpace(version) || !VersionPattern().IsMatch(version)))
            throw new ArgumentException("An exact valid package version is required for add or update.", nameof(version));

        var before = File.ReadAllText(project.PhysicalPath);
        var document = XDocument.Parse(before, LoadOptions.PreserveWhitespace);
        var reference = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "PackageReference" &&
            string.Equals((string?)element.Attribute("Include") ?? (string?)element.Attribute("Update"), packageId, StringComparison.OrdinalIgnoreCase));
        if (normalizedAction == "add")
        {
            if (reference is not null) throw new InvalidOperationException("The project already references that package; use update.");
            var itemGroup = document.Root!.Elements().FirstOrDefault(element => element.Name.LocalName == "ItemGroup") ?? new XElement(document.Root.Name.Namespace + "ItemGroup");
            if (itemGroup.Parent is null) document.Root.Add(itemGroup);
            itemGroup.Add(new XElement(document.Root.Name.Namespace + "PackageReference", new XAttribute("Include", packageId), new XAttribute("Version", version!)));
        }
        else if (normalizedAction == "update")
        {
            if (reference is null) throw new InvalidOperationException("The project does not reference that package; use add.");
            reference.SetAttributeValue("Version", version);
            reference.Elements().Where(element => element.Name.LocalName == "Version").Remove();
        }
        else
        {
            if (reference is null) throw new InvalidOperationException("The project does not reference that package.");
            reference.Remove();
        }

        var after = document.ToString(SaveOptions.DisableFormatting);
        var preview = $"--- {projectPath}\n+++ {projectPath}\n- {FindPackageLine(before, packageId)}\n+ {FindPackageLine(after, packageId)}";
        if (apply) File.WriteAllText(project.PhysicalPath, after);
        return Task.FromResult(new DependencyChangeResult(true,
            apply ? "The exact PackageReference change was applied; restore and verify before completion." : "PackageReference preview created without writing files.",
            projectPath, normalizedAction, packageId, version, preview, apply));
    }

    private static string FindPackageLine(string text, string packageId) =>
        text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Contains(packageId, StringComparison.OrdinalIgnoreCase)) ?? "(no package reference)";

    private static string ResolveDotNet() => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path && File.Exists(path) ? path : "dotnet";
}
