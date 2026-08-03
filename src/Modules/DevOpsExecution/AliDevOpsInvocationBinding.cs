using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Ali.Modules.Coding;
using Ali.Modules.Coding.Architecture;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Work;

namespace Ali.Modules.DevOpsExecution;

internal enum AliDevOpsInvocationKind
{
    ArchitectureInspect,
    ArchitectureCheck,
    QualityScan,
    ApplicationVerify,
    ReleasePublish,
    DeliveryVerify
}

/// <summary>
/// Closed production identities for the six architecture, quality, verification, release, and
/// delivery operations. These entries describe already-selected tools; they never route prose or
/// select an executor.
/// </summary>
internal static class AliDevOpsInvocationCatalog
{
    internal static IReadOnlyList<AliDevOpsInvocationKind> All { get; } =
        Array.AsReadOnly(new[]
        {
            AliDevOpsInvocationKind.ArchitectureInspect,
            AliDevOpsInvocationKind.ArchitectureCheck,
            AliDevOpsInvocationKind.QualityScan,
            AliDevOpsInvocationKind.ApplicationVerify,
            AliDevOpsInvocationKind.ReleasePublish,
            AliDevOpsInvocationKind.DeliveryVerify
        });

    internal static string ToolName(AliDevOpsInvocationKind kind) => kind switch
    {
        AliDevOpsInvocationKind.ArchitectureInspect =>
            AliCapabilityCatalog.ArchitectureInspectName,
        AliDevOpsInvocationKind.ArchitectureCheck =>
            AliCapabilityCatalog.ArchitectureCheckName,
        AliDevOpsInvocationKind.QualityScan =>
            AliCapabilityCatalog.DotNetQualityScanName,
        AliDevOpsInvocationKind.ApplicationVerify =>
            AliCapabilityCatalog.DotNetApplicationVerifyName,
        AliDevOpsInvocationKind.ReleasePublish =>
            AliCapabilityCatalog.DotNetReleasePublishName,
        AliDevOpsInvocationKind.DeliveryVerify =>
            AliCapabilityCatalog.DotNetDeliveryVerifyName,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static string OperationIdentity(AliDevOpsInvocationKind kind) => kind switch
    {
        AliDevOpsInvocationKind.ArchitectureInspect =>
            "ali.roslyn.architecture-inspect.v1",
        AliDevOpsInvocationKind.ArchitectureCheck =>
            "ali.roslyn.architecture-check-boundaries.v1",
        AliDevOpsInvocationKind.QualityScan =>
            "ali.roslyn.quality-scan-sarif.v1",
        AliDevOpsInvocationKind.ApplicationVerify =>
            "ali.application.smoke-test.v1",
        AliDevOpsInvocationKind.ReleasePublish =>
            "ali.dotnet.publish-release-manifest.v1",
        AliDevOpsInvocationKind.DeliveryVerify =>
            "ali.delivery.architecture-quality-build-test-application-release.v1",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static TimeSpan Timeout(AliDevOpsInvocationKind kind) => kind switch
    {
        AliDevOpsInvocationKind.ArchitectureInspect => TimeSpan.FromMinutes(15),
        AliDevOpsInvocationKind.ArchitectureCheck => TimeSpan.FromMinutes(15),
        AliDevOpsInvocationKind.QualityScan => TimeSpan.FromMinutes(20),
        AliDevOpsInvocationKind.ApplicationVerify => TimeSpan.FromMinutes(2),
        AliDevOpsInvocationKind.ReleasePublish => TimeSpan.FromMinutes(15),
        AliDevOpsInvocationKind.DeliveryVerify => TimeSpan.FromMinutes(60),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

internal sealed record AliDevOpsInvocationBinding(
    AliDevOpsInvocationKind Kind,
    string ToolName,
    string OperationIdentity,
    string ExecutorIdentity,
    string RootBinding,
    string DomainPreparationDigest,
    TargetStateSnapshot TargetState,
    IReadOnlyList<AliExecutionDirectoryBinding> TargetRootIdentities,
    AliExactProcessExecutionBinding ProcessBinding);

/// <summary>
/// Resolves typed arguments for one fixed production operation and binds them to approved physical
/// roots plus the existing executor identity. It does not create providers or choose an operation.
/// </summary>
internal sealed class AliDevOpsInvocationBindingResolver
{
    private const int MaximumBoundaryRules = 256;
    private const int MaximumBoundaryNamespaceCharacters = 1_024;
    private const long MaximumExecutorFileBytes = 128L * 1024 * 1024;
    private readonly AliCodingProjectResolver _resolver;

    internal AliDevOpsInvocationBindingResolver(AliCodingProjectResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    internal AliDevOpsInvocationBinding Resolve(
        AliDevOpsInvocationKind kind,
        JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("DevOps invocation arguments must be an object.");
        }

        var toolName = AliDevOpsInvocationCatalog.ToolName(kind);
        var operationIdentity = AliDevOpsInvocationCatalog.OperationIdentity(kind);
        var exact = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tool"] = toolName,
            ["operation"] = operationIdentity,
            ["timeoutMilliseconds"] = checked(
                    (long)AliDevOpsInvocationCatalog.Timeout(kind).TotalMilliseconds)
                .ToString(CultureInfo.InvariantCulture)
        };
        var roots = new List<(string Label, string PhysicalTarget, string Root)>();
        string executorIdentity;
        string? applicationArtifact = null;
        AliBoundExecutionFile? boundApplicationArtifact = null;
        AliApplicationLaunchClosure? applicationLaunchClosure = null;
        AliBoundExecutionFile? boundDotNetHost = null;
        AliPostBuildApplicationArtifactPolicy? postBuildApplicationArtifact = null;

        switch (kind)
        {
            case AliDevOpsInvocationKind.ArchitectureInspect:
            {
                RequireOnlyProperties(arguments, "targetPath");
                var targetPath = RequireString(arguments, "targetPath");
                var target = _resolver.ResolveExistingTarget(targetPath);
                roots.Add(("primary", target.PhysicalPath, target.RootDirectory));
                exact["targetPath"] = targetPath;
                exact["physicalTarget"] = NormalizePath(target.PhysicalPath);
                executorIdentity = RoslynExecutorIdentity();
                break;
            }
            case AliDevOpsInvocationKind.ArchitectureCheck:
            {
                RequireOnlyProperties(arguments, "targetPath", "rules");
                var targetPath = RequireString(arguments, "targetPath");
                var target = _resolver.ResolveExistingTarget(targetPath);
                var rules = RequireBoundaryRules(arguments);
                roots.Add(("primary", target.PhysicalPath, target.RootDirectory));
                exact["targetPath"] = targetPath;
                exact["physicalTarget"] = NormalizePath(target.PhysicalPath);
                exact["ruleCount"] = rules.Length.ToString(CultureInfo.InvariantCulture);
                for (var index = 0; index < rules.Length; index++)
                {
                    exact[$"rule.{index}.from"] = rules[index].FromNamespace;
                    exact[$"rule.{index}.mustNotReference"] =
                        rules[index].MustNotReferenceNamespace;
                }
                executorIdentity = RoslynExecutorIdentity();
                break;
            }
            case AliDevOpsInvocationKind.QualityScan:
            {
                RequireOnlyProperties(arguments, "projectPath");
                var projectPath = RequireString(arguments, "projectPath");
                var project = _resolver.ResolveExistingProject(projectPath);
                roots.Add(("primary", project.PhysicalPath, project.ProjectDirectory));
                exact["projectPath"] = projectPath;
                exact["physicalTarget"] = NormalizePath(project.PhysicalPath);
                exact["sarifRoot"] = NormalizePath(
                    Path.Combine(project.ProjectDirectory, ".ali", "quality"));
                executorIdentity = RoslynExecutorIdentity();
                break;
            }
            case AliDevOpsInvocationKind.ApplicationVerify:
            {
                RequireOnlyProperties(arguments, "projectPath", "configuration", "healthUrl");
                var projectPath = RequireString(arguments, "projectPath");
                var project = _resolver.ResolveExistingProject(projectPath);
                var configuration = NormalizeConfiguration(
                    OptionalString(arguments, "configuration"));
                var healthUrl = OptionalString(arguments, "healthUrl");
                ValidateHealthUrl(healthUrl);
                applicationArtifact = AliRoslynCodingTools.FindBuiltArtifact(
                    project.PhysicalPath,
                    configuration)
                    ?? throw new FileNotFoundException(
                        "Build the project before application verification.",
                        project.PhysicalPath);
                AliCodingProjectResolver.RejectReparsePoints(
                    project.MountRoot,
                    applicationArtifact);
                boundApplicationArtifact = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
                    applicationArtifact,
                    "The approved DevOps application artifact");
                if (Path.GetExtension(applicationArtifact).Equals(
                        ".dll",
                        StringComparison.OrdinalIgnoreCase))
                {
                    boundDotNetHost = CaptureDotNetHost();
                }
                applicationLaunchClosure = AliApplicationLaunchClosure.Capture(
                    boundApplicationArtifact);
                roots.Add(("primary", project.PhysicalPath, project.ProjectDirectory));
                exact["projectPath"] = projectPath;
                exact["physicalTarget"] = NormalizePath(project.PhysicalPath);
                exact["configuration"] = configuration;
                exact["healthUrl"] = healthUrl ?? "<null>";
                exact["applicationArtifact"] = NormalizePath(applicationArtifact);
                executorIdentity = ApplicationExecutorIdentity(
                    boundApplicationArtifact,
                    boundDotNetHost,
                    applicationLaunchClosure);
                break;
            }
            case AliDevOpsInvocationKind.ReleasePublish:
            {
                RequireOnlyProperties(
                    arguments,
                    "projectPath",
                    "runtimeIdentifier",
                    "selfContained");
                var projectPath = RequireString(arguments, "projectPath");
                var project = _resolver.ResolveExistingProject(projectPath);
                var runtimeIdentifier = NormalizeRuntime(
                    OptionalString(arguments, "runtimeIdentifier"));
                var selfContained = RequireBoolean(arguments, "selfContained");
                roots.Add(("primary", project.PhysicalPath, project.ProjectDirectory));
                exact["projectPath"] = projectPath;
                exact["physicalTarget"] = NormalizePath(project.PhysicalPath);
                exact["configuration"] = "Release";
                exact["runtimeIdentifier"] = runtimeIdentifier;
                exact["selfContained"] = selfContained.ToString(CultureInfo.InvariantCulture);
                exact["publishRoot"] = NormalizePath(
                    Path.Combine(project.ProjectDirectory, ".ali", "release"));
                boundDotNetHost = CaptureDotNetHost();
                executorIdentity = DotNetExecutorIdentity(boundDotNetHost);
                break;
            }
            case AliDevOpsInvocationKind.DeliveryVerify:
            {
                RequireOnlyProperties(
                    arguments,
                    "projectPath",
                    "testTargetPath",
                    "configuration",
                    "verifyApplication",
                    "publishRelease");
                var projectPath = RequireString(arguments, "projectPath");
                var project = _resolver.ResolveExistingProject(projectPath);
                var testTargetPath = OptionalString(arguments, "testTargetPath");
                var configuration = NormalizeConfiguration(
                    OptionalString(arguments, "configuration"));
                var verifyApplication = RequireBoolean(arguments, "verifyApplication");
                var publishRelease = RequireBoolean(arguments, "publishRelease");
                roots.Add(("primary", project.PhysicalPath, project.ProjectDirectory));
                exact["projectPath"] = projectPath;
                exact["physicalTarget"] = NormalizePath(project.PhysicalPath);
                exact["configuration"] = configuration;
                exact["verifyApplication"] =
                    verifyApplication.ToString(CultureInfo.InvariantCulture);
                exact["publishRelease"] =
                    publishRelease.ToString(CultureInfo.InvariantCulture);

                if (string.IsNullOrWhiteSpace(testTargetPath))
                {
                    exact["testTargetPath"] = "<primary>";
                }
                else
                {
                    var testTarget = _resolver.ResolveExistingTarget(testTargetPath);
                    roots.Add(("test", testTarget.PhysicalPath, testTarget.RootDirectory));
                    exact["testTargetPath"] = testTargetPath;
                    exact["physicalTestTarget"] = NormalizePath(testTarget.PhysicalPath);
                }

                if (verifyApplication)
                {
                    postBuildApplicationArtifact = BindPostBuildApplicationArtifact(
                        projectPath,
                        project,
                        configuration);
                }
                if (publishRelease)
                {
                    // These are the existing fixed arguments used by AliAutonomousDelivery.
                    exact["releaseRuntimeIdentifier"] = "win-x64";
                    exact["releaseSelfContained"] = "True";
                }
                boundDotNetHost = CaptureDotNetHost();
                executorIdentity = DeliveryExecutorIdentity(
                    boundDotNetHost,
                    postBuildApplicationArtifact);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        exact["dotnetHost.path"] = boundDotNetHost?.PhysicalPath ?? "<not-used>";
        exact["dotnetHost.identity"] = boundDotNetHost?.Identity ?? "<not-used>";
        exact["applicationArtifact.path"] =
            boundApplicationArtifact?.PhysicalPath ?? "<not-used>";
        exact["applicationArtifact.identity"] =
            boundApplicationArtifact?.Identity ?? "<not-used>";
        if (applicationLaunchClosure is null)
        {
            exact["applicationOutput.path"] = "<not-used>";
            exact["applicationOutput.identity"] = "<not-used>";
        }
        else
        {
            applicationLaunchClosure.AddTo(exact, "applicationOutput");
        }
        if (postBuildApplicationArtifact is null)
        {
            exact["postBuildApplication.policy"] = "<not-used>";
        }
        else
        {
            postBuildApplicationArtifact.AddTo(exact);
        }
        exact["executor"] = executorIdentity;
        var targetRootIdentities = roots
            .Select(root => root.Root)
            .Distinct(PathComparer)
            .Order(PathComparer)
            .Select(root => AliExecutionDirectoryBinding.Capture(
                root,
                "A selected DevOps target root spine"))
            .ToArray();
        for (var index = 0; index < targetRootIdentities.Length; index++)
        {
            targetRootIdentities[index].AddTo(
                exact,
                $"targetRootSpine.{index.ToString("D3", CultureInfo.InvariantCulture)}");
        }
        var targetState = AliDevOpsInputFingerprint.Capture(
            roots,
            applicationArtifact);
        return new AliDevOpsInvocationBinding(
            kind,
            toolName,
            operationIdentity,
            executorIdentity,
            RootBinding(roots),
            WorkIdentityCanonicalizer.MapDigest(
                "devops-exact-invocation-binding-v1",
                exact),
            targetState,
            Array.AsReadOnly(targetRootIdentities),
            new AliExactProcessExecutionBinding(
                boundDotNetHost,
                boundApplicationArtifact,
                applicationLaunchClosure,
                postBuildApplicationArtifact));
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    internal static string TargetVersionDigest(TargetStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return WorkIdentityCanonicalizer.MapDigest(
            "action-target-versions-v1",
            snapshot.TargetVersions);
    }

    private static ArchitectureBoundaryRule[] RequireBoundaryRules(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("rules", out var rules)
            || rules.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The exact 'rules' argument must be an array.");
        }
        var length = rules.GetArrayLength();
        if (length is < 1 or > MaximumBoundaryRules)
        {
            throw new InvalidDataException(
                $"Architecture boundary checks require 1-{MaximumBoundaryRules} exact rules.");
        }

        var result = new ArchitectureBoundaryRule[length];
        var index = 0;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Each architecture boundary rule must be an object.");
            }
            string? from = null;
            string? mustNotReference = null;
            foreach (var property in rule.EnumerateObject())
            {
                if (property.Name.Equals("FromNamespace", StringComparison.OrdinalIgnoreCase))
                {
                    if (from is not null || property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException(
                            "Each boundary rule must contain one string FromNamespace.");
                    }
                    from = property.Value.GetString();
                }
                else if (property.Name.Equals(
                             "MustNotReferenceNamespace",
                             StringComparison.OrdinalIgnoreCase))
                {
                    if (mustNotReference is not null
                        || property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException(
                            "Each boundary rule must contain one string MustNotReferenceNamespace.");
                    }
                    mustNotReference = property.Value.GetString();
                }
                else
                {
                    throw new InvalidDataException(
                        $"Architecture boundary rule property '{property.Name}' is not allowed.");
                }
            }

            RequireBoundaryNamespace(from, "FromNamespace");
            RequireBoundaryNamespace(mustNotReference, "MustNotReferenceNamespace");
            result[index++] = new ArchitectureBoundaryRule(from!, mustNotReference!);
        }
        return result;
    }

    private static void RequireBoundaryNamespace(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumBoundaryNamespaceCharacters)
        {
            throw new InvalidDataException(
                $"Architecture boundary {name} must contain 1-{MaximumBoundaryNamespaceCharacters} characters.");
        }
    }

    private static string RoslynExecutorIdentity() =>
        "roslyn-msbuild:" + string.Join(
            ":",
            FileIdentity(typeof(AliRoslynCodingTools).Assembly.Location),
            FileIdentity(typeof(Microsoft.CodeAnalysis.Compilation).Assembly.Location),
            FileIdentity(typeof(Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace).Assembly.Location));

    private static string ApplicationExecutorIdentity(
        AliBoundExecutionFile artifact,
        AliBoundExecutionFile? dotNetHost,
        AliApplicationLaunchClosure launchClosure)
    {
        var identity = "application-artifact:"
            + artifact.PhysicalPath
            + ":"
            + artifact.Identity
            + ":application-output:"
            + launchClosure.OutputDirectoryPath
            + ":"
            + launchClosure.Identity;
        return Path.GetExtension(artifact.PhysicalPath).Equals(
                ".dll",
                StringComparison.OrdinalIgnoreCase)
            ? identity + ":dotnet-host:" + DotNetExecutorIdentity(
                dotNetHost
                ?? throw new InvalidDataException(
                    "A managed application verification has no exact .NET host binding."))
            : identity;
    }

    private static string DeliveryExecutorIdentity(
        AliBoundExecutionFile dotNetHost,
        AliPostBuildApplicationArtifactPolicy? postBuildApplicationArtifact) =>
        string.Join(
            ":",
            RoslynExecutorIdentity(),
            "dotnet-host",
            DotNetExecutorIdentity(dotNetHost),
            postBuildApplicationArtifact is null
                ? "application-not-requested"
                : PostBuildApplicationExecutorIdentity(postBuildApplicationArtifact));

    private static string PostBuildApplicationExecutorIdentity(
        AliPostBuildApplicationArtifactPolicy policy)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        policy.AddTo(values);
        return "post-build-application:"
            + WorkIdentityCanonicalizer.MapDigest(
                "devops-post-build-application-policy-v1",
                values);
    }

    private static AliPostBuildApplicationArtifactPolicy BindPostBuildApplicationArtifact(
        string projectArgument,
        AliResolvedCodingProject project,
        string configuration)
    {
        var descriptor = ReadStaticApplicationDescriptor(project.PhysicalPath);
        var outputRoot = Path.GetFullPath(Path.Combine(
            project.ProjectDirectory,
            "bin",
            configuration,
            descriptor.TargetFramework));
        RequireWithin(
            project.MountRoot,
            outputRoot,
            "The static delivery application output root");
        AliCodingProjectResolver.RejectReparsePoints(project.MountRoot, outputRoot);

        var candidates = new List<string>(capacity: 2);
        candidates.Add(Path.Combine(
            outputRoot,
            descriptor.AssemblyName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)));
        candidates.Add(Path.Combine(outputRoot, descriptor.AssemblyName + ".dll"));
        return AliPostBuildApplicationArtifactPolicy.Create(
            projectArgument,
            project.PhysicalPath,
            configuration,
            outputRoot,
            candidates);
    }

    private static StaticApplicationDescriptor ReadStaticApplicationDescriptor(
        string projectPath)
    {
        using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            Path.GetFullPath(projectPath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            "The delivery project is not a regular local file.");
        var fileIdentity = WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
            stream,
            projectPath,
            "The delivery project does not have a stable single-link file identity.");
        if (stream.Length < 0 || stream.Length > MaximumExecutorFileBytes)
        {
            throw new InvalidDataException(
                "The delivery project exceeds its fixed file-size bound.");
        }
        var expectedLength = stream.Length;
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(
                stream,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaximumExecutorFileBytes,
                    CloseInput = false
                });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException(
                "The delivery project is not bounded well-formed XML.",
                exception);
        }
        if (stream.Position != expectedLength || stream.Length != expectedLength)
        {
            throw new InvalidDataException(
                "The delivery project changed while its static artifact policy was captured.");
        }
        RequireStableFileIdentity(
            fileIdentity,
            WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
                stream,
                projectPath,
                "The delivery project does not have a stable single-link file identity."),
            "The delivery project changed file identity while its static artifact policy was captured.");

        var targetFramework = ReadSingleStaticProperty(document, "TargetFramework");
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            var targetFrameworks = ReadSingleStaticProperty(document, "TargetFrameworks");
            var values = targetFrameworks?
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (values is not { Length: 1 })
            {
                throw new InvalidDataException(
                    "Delivery application verification requires one literal TargetFramework in the selected project file.");
            }
            targetFramework = values[0];
        }
        ValidateStaticPathSegment(targetFramework, "TargetFramework");

        var assemblyName = ReadSingleStaticProperty(document, "AssemblyName")
            ?? Path.GetFileNameWithoutExtension(projectPath);
        ValidateStaticPathSegment(assemblyName, "AssemblyName");
        return new StaticApplicationDescriptor(targetFramework, assemblyName);
    }

    private static string? ReadSingleStaticProperty(XDocument document, string propertyName)
    {
        var values = document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals(
                propertyName,
                StringComparison.Ordinal))
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (values.Length > 1)
        {
            throw new InvalidDataException(
                $"Delivery application verification requires one literal {propertyName} in the selected project file.");
        }
        if (values.Length == 0)
        {
            return null;
        }
        if (values[0].Contains("$(", StringComparison.Ordinal)
            || values[0].Contains("%(", StringComparison.Ordinal)
            || values[0].Contains("@(", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Delivery application verification does not evaluate dynamic {propertyName} declarations before authorization.");
        }
        return values[0];
    }

    private static void ValidateStaticPathSegment(string value, string propertyName)
    {
        if (value.Length > 256
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidDataException(
                $"The literal delivery {propertyName} is not a safe path segment.");
        }
    }

    private sealed record StaticApplicationDescriptor(
        string TargetFramework,
        string AssemblyName);

    private static void RequireWithin(string root, string path, string description)
    {
        if (!IsWithin(root, path))
        {
            throw new InvalidDataException(description + " escapes its authorized root.");
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(normalizedRoot, normalizedPath, comparison)
            || normalizedPath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                comparison);
    }

    private static AliBoundExecutionFile CaptureDotNetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var path = !string.IsNullOrWhiteSpace(configured) && File.Exists(configured)
            ? Path.GetFullPath(configured)
            : AliCodingExecutionAssetFingerprint.ResolveRequiredExecutable(
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            path,
            "The fixed DevOps .NET host");
    }

    private static string DotNetExecutorIdentity(AliBoundExecutionFile host) =>
        host.PhysicalPath + ":" + host.Identity;

    private static string? ResolvePathExecutable(string fileName)
    {
        foreach (var segment in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(segment, fileName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (Exception exception) when (exception is ArgumentException
                                               or IOException
                                               or NotSupportedException)
            {
                // Invalid PATH entries are not accepted executor identities.
            }
        }
        return null;
    }

    private static string FileIdentity(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            "A DevOps executor is not a regular local file.");
        var fileIdentity = WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
            stream,
            fullPath,
            "A DevOps executor does not have a stable single-link file identity.");
        if (stream.Length < 0 || stream.Length > MaximumExecutorFileBytes)
        {
            throw new InvalidDataException(
                "A DevOps executor exceeds its fixed file-size bound.");
        }
        var expectedLength = stream.Length;
        var identity = HashStream(stream);
        if (stream.Position != expectedLength || stream.Length != expectedLength)
        {
            throw new InvalidDataException(
                "A DevOps executor changed while its exact identity was captured.");
        }
        RequireStableFileIdentity(
            fileIdentity,
            WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
                stream,
                fullPath,
                "A DevOps executor does not have a stable single-link file identity."),
            "A DevOps executor changed file identity while it was captured.");
        return HashText(string.Join(
            "\0",
            NormalizePath(fullPath),
            fileIdentity.CanonicalIdentity,
            identity));
    }

    private static string RootBinding(
        IReadOnlyList<(string Label, string PhysicalTarget, string Root)> roots)
    {
        if (roots.Count == 0)
        {
            throw new InvalidDataException("The exact DevOps invocation has no approved root.");
        }
        return HashText(string.Join(
            "\0",
            new[] { "ali-devops-root-binding-v1" }.Concat(
                roots.Select(root =>
                    root.Label + ":" + NormalizePath(root.Root)))));
    }

    private static void RequireOnlyProperties(
        JsonElement arguments,
        params string[] allowedProperties)
    {
        var allowed = allowedProperties.ToFrozenSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"DevOps argument property '{property.Name}' is not allowed or is duplicated.");
            }
        }
    }

    private static string RequireString(JsonElement arguments, string propertyName)
    {
        var value = OptionalString(arguments, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"The exact '{propertyName}' DevOps argument is required.");
        }
        return value;
    }

    private static string? OptionalString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"The exact '{propertyName}' DevOps argument must be a string or null.");
        }
        return value.GetString();
    }

    private static bool RequireBoolean(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"The exact '{propertyName}' DevOps argument must be a boolean.");
        }
        return value.GetBoolean();
    }

    private static string NormalizeConfiguration(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "Release"
            : value.Trim() switch
            {
                var text when text.Equals("Debug", StringComparison.OrdinalIgnoreCase) =>
                    "Debug",
                var text when text.Equals("Release", StringComparison.OrdinalIgnoreCase) =>
                    "Release",
                _ => throw new ArgumentException(
                    "Configuration must be Debug or Release.",
                    nameof(value))
            };

    private static string NormalizeRuntime(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "win-x64"
            : value.Trim() switch
            {
                "win-x64" => "win-x64",
                "win-arm64" => "win-arm64",
                _ => throw new ArgumentException(
                    "Runtime must be win-x64 or win-arm64.",
                    nameof(value))
            };

    private static void ValidateHealthUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !uri.IsLoopback)
        {
            throw new ArgumentException(
                "Health URL must be an absolute loopback HTTP or HTTPS URL.",
                nameof(value));
        }
    }

    private static string NormalizePath(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static void RequireStableFileIdentity(
        WindowsOrchestrationFileBoundary.RegularFileIdentity expected,
        WindowsOrchestrationFileBoundary.RegularFileIdentity actual,
        string message)
    {
        if (!string.Equals(
                expected.CanonicalIdentity,
                actual.CanonicalIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(message);
        }
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string HashStream(Stream stream)
    {
        var hash = SHA256.HashData(stream);
        try
        {
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }
}

internal sealed class AliDevOpsTargetStateAdapter : IActionTargetStateAdapter
{
    private static readonly FrozenDictionary<string, AliDevOpsInvocationKind> KindsByTool =
        AliDevOpsInvocationCatalog.All.ToFrozenDictionary(
            AliDevOpsInvocationCatalog.ToolName,
            kind => kind,
            StringComparer.Ordinal);
    private readonly AliDevOpsInvocationBindingResolver _bindings;

    internal AliDevOpsTargetStateAdapter(AliDevOpsInvocationBindingResolver bindings)
    {
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
    }

    public IReadOnlyCollection<string> ToolNames { get; } =
        Array.AsReadOnly(AliDevOpsInvocationCatalog.All
            .Select(AliDevOpsInvocationCatalog.ToolName)
            .Order(StringComparer.Ordinal)
            .ToArray());

    public TargetStateSnapshot Capture(string toolName, JsonElement arguments)
    {
        if (!KindsByTool.TryGetValue(toolName, out var kind))
        {
            throw new InvalidOperationException(
                "The DevOps target-state adapter has no exact registration for this tool.");
        }
        return _bindings.Resolve(kind, arguments).TargetState;
    }
}

/// <summary>
/// Bounded, no-follow fingerprint of source inputs and any exact built application artifact that
/// an operation can execute. Output/cache roots are excluded from source trees and added only when
/// they are an explicit execution input.
/// </summary>
internal static class AliDevOpsInputFingerprint
{
    private const int MaximumEntries = 16_000;
    private const int MaximumFiles = 12_000;
    private const long MaximumAggregateBytes = 512L * 1024 * 1024;
    private const long MaximumFileBytes = 128L * 1024 * 1024;
    private static readonly FrozenSet<string> ExcludedDirectoryNames = new[]
    {
        ".git",
        ".vs",
        ".idea",
        ".ali",
        "bin",
        "obj",
        "node_modules",
        "artifacts",
        "release",
        "TestResults"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static TargetStateSnapshot Capture(
        IReadOnlyList<(string Label, string PhysicalTarget, string Root)> roots,
        string? applicationArtifact)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in roots)
        {
            versions[$"{item.Label}-target-file-v1"] = HashSingleFile(item.PhysicalTarget);
            versions[$"{item.Label}-input-tree-v1"] = HashTree(
                item.Root,
                excludeSourceOutputs: true);
        }

        if (applicationArtifact is not null)
        {
            versions["application-artifact-file-v1"] = HashSingleFile(applicationArtifact);
            versions["application-output-tree-v1"] = HashTree(
                Path.GetDirectoryName(applicationArtifact)
                    ?? throw new InvalidDataException(
                        "The application artifact has no containing directory."),
                excludeSourceOutputs: false);
        }

        var frozen = versions.ToFrozenDictionary(StringComparer.Ordinal);
        return new TargetStateSnapshot(
            frozen,
            frozen,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static string HashSingleFile(string path)
    {
        var count = 0;
        long bytes = 0;
        return HashFile(path, ref count, ref bytes);
    }

    private static string HashTree(string root, bool excludeSourceOutputs)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            canonicalRoot,
            "A DevOps input root is not a regular local directory.");
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pending = new Stack<string>();
        pending.Push(canonicalRoot);
        var entriesSeen = 0;
        var fileCount = 0;
        long bytes = 0;

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var entries = Directory.EnumerateFileSystemEntries(directory)
                .Take(MaximumEntries + 1)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (entries.Length > MaximumEntries)
            {
                throw new InvalidDataException(
                    "A DevOps input directory exceeds its fixed entry-count bound.");
            }
            var childDirectories = new List<string>();
            foreach (var entry in entries)
            {
                entriesSeen = checked(entriesSeen + 1);
                if (entriesSeen > MaximumEntries)
                {
                    throw new InvalidDataException(
                        "The DevOps input tree exceeds its fixed entry-count bound.");
                }
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new InvalidDataException(
                        "A DevOps input tree contains a reparse point or device entry.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!excludeSourceOutputs
                        || !ExcludedDirectoryNames.Contains(Path.GetFileName(entry)))
                    {
                        childDirectories.Add(entry);
                    }
                    continue;
                }

                var relative = Path.GetRelativePath(canonicalRoot, entry).Replace('\\', '/');
                Append(aggregate, relative);
                Append(aggregate, HashFile(entry, ref fileCount, ref bytes));
            }

            for (var index = childDirectories.Count - 1; index >= 0; index--)
            {
                pending.Push(childDirectories[index]);
            }
        }

        Append(aggregate, entriesSeen.ToString(CultureInfo.InvariantCulture));
        Append(aggregate, fileCount.ToString(CultureInfo.InvariantCulture));
        Append(aggregate, bytes.ToString(CultureInfo.InvariantCulture));
        var hash = aggregate.GetHashAndReset();
        try
        {
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static string HashFile(
        string path,
        ref int fileCount,
        ref long aggregateBytes)
    {
        fileCount = checked(fileCount + 1);
        if (fileCount > MaximumFiles)
        {
            throw new InvalidDataException(
                "The DevOps input tree exceeds its fixed file-count bound.");
        }
        using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            "A DevOps input is not a regular local file.");
        var fileIdentity = WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
            stream,
            path,
            "A DevOps input does not have a stable single-link file identity.");
        var length = stream.Length;
        if (length < 0 || length > MaximumFileBytes)
        {
            throw new InvalidDataException(
                "A DevOps input file exceeds its fixed size bound.");
        }
        aggregateBytes = checked(aggregateBytes + length);
        if (aggregateBytes > MaximumAggregateBytes)
        {
            throw new InvalidDataException(
                "The DevOps input tree exceeds its fixed aggregate size bound.");
        }
        var hash = SHA256.HashData(stream);
        try
        {
            if (stream.Position != length || stream.Length != length)
            {
                throw new InvalidDataException(
                    "A DevOps input changed while its exact hash was captured.");
            }
            RequireStableFileIdentity(
                fileIdentity,
                WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
                    stream,
                    path,
                    "A DevOps input does not have a stable single-link file identity."),
                "A DevOps input changed file identity while it was captured.");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
                    "\0",
                    fileIdentity.CanonicalIdentity,
                    Convert.ToHexString(hash).ToLowerInvariant()))))
                .ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static void RequireStableFileIdentity(
        WindowsOrchestrationFileBoundary.RegularFileIdentity expected,
        WindowsOrchestrationFileBoundary.RegularFileIdentity actual,
        string message)
    {
        if (!string.Equals(
                expected.CanonicalIdentity,
                actual.CanonicalIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(message);
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            hash.AppendData(bytes);
            hash.AppendData([0]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
