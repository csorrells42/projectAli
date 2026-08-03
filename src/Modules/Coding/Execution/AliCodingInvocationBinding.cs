using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coding.Languages;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.WorkstationFiles;

namespace Ali.Modules.Coding.Execution;

internal enum AliCodingInvocationKind
{
    ProviderAnalyze,
    ProviderFormat,
    ProviderBuild,
    ProviderTest,
    ProviderRun,
    DotNetCreate,
    RoslynFormat,
    DotNetBuild,
    DotNetTest,
    DotNetVerify,
    DotNetRun,
    DotNetStop,
    DependencyInspect,
    DependencyApply
}

/// <summary>
/// Exact, closed catalog for the ordinary coding/process effect adapters. This is registry
/// identity, not user-text routing: every entry names one concrete production tool schema.
/// </summary>
internal static class AliCodingInvocationCatalog
{
    internal static IReadOnlyList<AliCodingInvocationKind> All { get; } =
        Array.AsReadOnly(new[]
        {
            AliCodingInvocationKind.ProviderAnalyze,
            AliCodingInvocationKind.ProviderFormat,
            AliCodingInvocationKind.ProviderBuild,
            AliCodingInvocationKind.ProviderTest,
            AliCodingInvocationKind.ProviderRun,
            AliCodingInvocationKind.DotNetCreate,
            AliCodingInvocationKind.RoslynFormat,
            AliCodingInvocationKind.DotNetBuild,
            AliCodingInvocationKind.DotNetTest,
            AliCodingInvocationKind.DotNetVerify,
            AliCodingInvocationKind.DotNetRun,
            AliCodingInvocationKind.DotNetStop,
            AliCodingInvocationKind.DependencyInspect,
            AliCodingInvocationKind.DependencyApply
        });

    internal static string ToolName(AliCodingInvocationKind kind) => kind switch
    {
        AliCodingInvocationKind.ProviderAnalyze => AliCapabilityCatalog.CodingAnalyzeProjectName,
        AliCodingInvocationKind.ProviderFormat => AliCapabilityCatalog.CodingFormatProjectName,
        AliCodingInvocationKind.ProviderBuild => AliCapabilityCatalog.CodingBuildProjectName,
        AliCodingInvocationKind.ProviderTest => AliCapabilityCatalog.CodingTestProjectName,
        AliCodingInvocationKind.ProviderRun => AliCapabilityCatalog.CodingRunProjectName,
        AliCodingInvocationKind.DotNetCreate => AliCapabilityCatalog.DotNetCreateProjectName,
        AliCodingInvocationKind.RoslynFormat => AliCapabilityCatalog.RoslynFormatProjectName,
        AliCodingInvocationKind.DotNetBuild => AliCapabilityCatalog.DotNetBuildName,
        AliCodingInvocationKind.DotNetTest => AliCapabilityCatalog.DotNetTestName,
        AliCodingInvocationKind.DotNetVerify => AliCapabilityCatalog.DotNetVerifyName,
        AliCodingInvocationKind.DotNetRun => AliCapabilityCatalog.DotNetRunName,
        AliCodingInvocationKind.DotNetStop => AliCapabilityCatalog.DotNetStopProjectName,
        AliCodingInvocationKind.DependencyInspect => AliCapabilityCatalog.DotNetDependencyInspectName,
        AliCodingInvocationKind.DependencyApply => AliCapabilityCatalog.DotNetDependencyApplyName,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static string CommandIdentity(AliCodingInvocationKind kind) => kind switch
    {
        AliCodingInvocationKind.ProviderAnalyze => "ali.provider.analyze.v1",
        AliCodingInvocationKind.ProviderFormat => "ali.provider.format.v1",
        AliCodingInvocationKind.ProviderBuild => "ali.provider.build.v1",
        AliCodingInvocationKind.ProviderTest => "ali.provider.test.v1",
        AliCodingInvocationKind.ProviderRun => "ali.provider.run.v1",
        AliCodingInvocationKind.DotNetCreate => "ali.dotnet.new.v1",
        AliCodingInvocationKind.RoslynFormat => "ali.roslyn.format-project.v1",
        AliCodingInvocationKind.DotNetBuild => "ali.msbuild.restore-build.v1",
        AliCodingInvocationKind.DotNetTest => "ali.dotnet.test-trx.v1",
        AliCodingInvocationKind.DotNetVerify => "ali.dotnet.build-test-verify.v1",
        AliCodingInvocationKind.DotNetRun => "ali.dotnet.run-artifact.v1",
        AliCodingInvocationKind.DotNetStop => "ali.dotnet.stop-project.v1",
        AliCodingInvocationKind.DependencyInspect => "ali.dotnet.package-audit.v1",
        AliCodingInvocationKind.DependencyApply => "ali.package-reference.apply.v1",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static TimeSpan Timeout(AliCodingInvocationKind kind) => kind switch
    {
        AliCodingInvocationKind.ProviderAnalyze => TimeSpan.FromMinutes(15),
        AliCodingInvocationKind.ProviderFormat => TimeSpan.FromMinutes(15),
        AliCodingInvocationKind.ProviderBuild => TimeSpan.FromMinutes(20),
        AliCodingInvocationKind.ProviderTest => TimeSpan.FromMinutes(20),
        AliCodingInvocationKind.ProviderRun => TimeSpan.FromMinutes(5),
        AliCodingInvocationKind.DotNetCreate => TimeSpan.FromMinutes(5),
        AliCodingInvocationKind.RoslynFormat => TimeSpan.FromMinutes(15),
        AliCodingInvocationKind.DotNetBuild => TimeSpan.FromMinutes(20),
        AliCodingInvocationKind.DotNetTest => TimeSpan.FromMinutes(20),
        AliCodingInvocationKind.DotNetVerify => TimeSpan.FromMinutes(30),
        AliCodingInvocationKind.DotNetRun => TimeSpan.FromMinutes(2),
        AliCodingInvocationKind.DotNetStop => TimeSpan.FromMinutes(1),
        AliCodingInvocationKind.DependencyInspect => TimeSpan.FromMinutes(10),
        AliCodingInvocationKind.DependencyApply => TimeSpan.FromMinutes(2),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static string CapabilityGroup(AliCodingInvocationKind kind) => kind switch
    {
        AliCodingInvocationKind.ProviderAnalyze
            or AliCodingInvocationKind.ProviderFormat
            or AliCodingInvocationKind.ProviderBuild
            or AliCodingInvocationKind.ProviderTest
            or AliCodingInvocationKind.ProviderRun => "programming-core",
        _ => "csharp-dotnet-roslyn"
    };

    internal static string EffectKind(AliCodingInvocationKind kind) => kind switch
    {
        AliCodingInvocationKind.DotNetCreate => "create",
        AliCodingInvocationKind.ProviderFormat
            or AliCodingInvocationKind.RoslynFormat
            or AliCodingInvocationKind.DependencyApply => "update",
        _ => "execute"
    };
}

internal sealed record AliCodingInvocationBinding(
    AliCodingInvocationKind Kind,
    string ToolName,
    string CommandIdentity,
    string ExecutorIdentity,
    string TargetRoot,
    AliExecutionDirectoryBinding TargetRootIdentity,
    string RootBinding,
    string DomainPreparationDigest,
    TargetStateSnapshot TargetState,
    AliCodingRuntimeBinding RuntimeBinding,
    IReadOnlyDictionary<string, string> ExecutionAssets);

/// <summary>
/// Resolves only the typed arguments of one already-selected exact tool. It does not select a
/// tool, provider, executable, or operation from user prose.
/// </summary>
internal sealed class AliCodingInvocationBindingResolver
{
    private readonly AliWorkstationFileAccess _fileAccess;
    private readonly AliCodingProjectResolver _dotNetResolver;
    private readonly AliLanguageProjectResolver _languageResolver;
    private readonly AliLanguageProviderRegistry _languageProviders;
    private readonly AliRoslynCodingTools _tools;

    internal AliCodingInvocationBindingResolver(
        AliWorkstationFileAccess fileAccess,
        AliCodingProjectResolver dotNetResolver,
        AliLanguageProjectResolver languageResolver,
        AliLanguageProviderRegistry languageProviders,
        AliRoslynCodingTools tools)
    {
        _fileAccess = fileAccess ?? throw new ArgumentNullException(nameof(fileAccess));
        _dotNetResolver = dotNetResolver ?? throw new ArgumentNullException(nameof(dotNetResolver));
        _languageResolver = languageResolver ?? throw new ArgumentNullException(nameof(languageResolver));
        _languageProviders = languageProviders ?? throw new ArgumentNullException(nameof(languageProviders));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
    }

    internal AliCodingInvocationBinding Resolve(
        AliCodingInvocationKind kind,
        JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Coding invocation arguments must be an object.");
        }

        var toolName = AliCodingInvocationCatalog.ToolName(kind);
        var commandIdentity = AliCodingInvocationCatalog.CommandIdentity(kind);
        string targetPath;
        string physicalTarget;
        string targetRoot;
        string executorIdentity;
        var runtimeBinding = AliCodingRuntimeBinding.None;
        var executionState = new Dictionary<string, string>(StringComparer.Ordinal);
        var executionAssets = new Dictionary<string, string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        var exactArguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tool"] = toolName,
            ["command"] = commandIdentity,
            ["timeoutMilliseconds"] = checked((long)AliCodingInvocationCatalog.Timeout(kind).TotalMilliseconds)
                .ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        switch (kind)
        {
            case AliCodingInvocationKind.ProviderAnalyze:
            case AliCodingInvocationKind.ProviderFormat:
            case AliCodingInvocationKind.ProviderBuild:
            case AliCodingInvocationKind.ProviderTest:
            case AliCodingInvocationKind.ProviderRun:
            {
                targetPath = RequireString(arguments, "targetPath");
                var project = _languageResolver.Resolve(targetPath);
                var provider = _languageProviders.Resolve(project);
                RequireProviderExecutionCanBeBound(kind, provider.Id);
                physicalTarget = project.PhysicalPath;
                targetRoot = project.ProjectDirectory;
                var providerIdentity = ProviderIdentity(
                    kind,
                    provider,
                    project,
                    executionState,
                    executionAssets);
                executorIdentity = providerIdentity.Identity;
                exactArguments["targetPath"] = targetPath;
                exactArguments["configuration"] = kind is AliCodingInvocationKind.ProviderBuild
                    or AliCodingInvocationKind.ProviderTest
                    or AliCodingInvocationKind.ProviderRun
                    ? OptionalString(arguments, "configuration") ?? "<null>"
                    : "<not-applicable>";
                exactArguments["provider"] = provider.Id;
                if (project.Language == AliProgrammingLanguage.CSharp)
                {
                    if (kind == AliCodingInvocationKind.ProviderRun)
                    {
                        var projectManifestPath = Path.Combine(
                            project.ProjectDirectory,
                            project.ManifestName);
                        var run = _tools.CaptureRunExecutionBinding(
                            projectManifestPath,
                            OptionalString(arguments, "configuration"));
                        runtimeBinding = new AliCodingRuntimeBinding(null, run, null);
                        executorIdentity = RunExecutorIdentity(run);
                        AddRuntimeAssets(runtimeBinding, executionAssets);
                    }
                    else if (kind == AliCodingInvocationKind.ProviderTest)
                    {
                        var host = providerIdentity.DotNetHost
                            ?? throw new InvalidDataException(
                                "The selected .NET provider has no exact dotnet host binding.");
                        runtimeBinding = new AliCodingRuntimeBinding(host, null, null);
                        executorIdentity = "dotnet-sdk:" + ExecutionFileIdentity(host);
                        AddRuntimeAssets(runtimeBinding, executionAssets);
                    }
                }
                break;
            }
            case AliCodingInvocationKind.DotNetCreate:
            {
                targetPath = RequireString(arguments, "projectPath");
                var template = RequireString(arguments, "template");
                var resolved = _fileAccess.ResolvePhysicalFilePath(targetPath);
                physicalTarget = resolved.PhysicalPath;
                if (!Path.GetExtension(physicalTarget).Equals(
                        ".csproj",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "The exact .NET creation target must be a .csproj path.");
                }
                targetRoot = Path.GetDirectoryName(physicalTarget)
                    ?? throw new InvalidDataException(
                        "The exact .NET creation target has no parent directory.");
                var host = BindCurrentDotNetHost(executionAssets);
                runtimeBinding = new AliCodingRuntimeBinding(host, null, null);
                executorIdentity = "dotnet-sdk:" + ExecutionFileIdentity(host);
                exactArguments["projectPath"] = targetPath;
                exactArguments["template"] = template;
                exactArguments["framework"] = "net10.0";
                exactArguments["restore"] = "disabled";
                break;
            }
            case AliCodingInvocationKind.RoslynFormat:
            case AliCodingInvocationKind.DotNetRun:
            case AliCodingInvocationKind.DotNetStop:
            case AliCodingInvocationKind.DependencyInspect:
            case AliCodingInvocationKind.DependencyApply:
            {
                targetPath = RequireString(arguments, "projectPath");
                var project = _dotNetResolver.ResolveExistingProject(targetPath);
                physicalTarget = project.PhysicalPath;
                targetRoot = project.ProjectDirectory;
                executorIdentity = kind switch
                {
                    AliCodingInvocationKind.DotNetRun
                        or AliCodingInvocationKind.DotNetStop => "pending-runtime-binding",
                    AliCodingInvocationKind.DependencyInspect =>
                        BindDotNetHostExecutor(executionAssets, out runtimeBinding),
                    _ => DotNetExecutorIdentity(kind, executionAssets)
                };
                exactArguments["projectPath"] = targetPath;
                exactArguments["configuration"] = kind is AliCodingInvocationKind.DotNetBuild
                    or AliCodingInvocationKind.DotNetRun
                    or AliCodingInvocationKind.DotNetStop
                    ? OptionalString(arguments, "configuration") ?? "<null>"
                    : "<not-applicable>";
                if (kind == AliCodingInvocationKind.DependencyApply)
                {
                    exactArguments["action"] = RequireString(arguments, "action");
                    exactArguments["packageId"] = RequireString(arguments, "packageId");
                    exactArguments["version"] = OptionalString(arguments, "version") ?? "<null>";
                }
                if (kind == AliCodingInvocationKind.DotNetRun)
                {
                    var run = _tools.CaptureRunExecutionBinding(
                        physicalTarget,
                        OptionalString(arguments, "configuration"));
                    runtimeBinding = new AliCodingRuntimeBinding(null, run, null);
                    executorIdentity = RunExecutorIdentity(run);
                    AddRuntimeAssets(runtimeBinding, executionAssets);
                }
                else if (kind == AliCodingInvocationKind.DotNetStop)
                {
                    var stop = _tools.CaptureStopExecutionBinding(
                        physicalTarget,
                        OptionalString(arguments, "configuration"));
                    runtimeBinding = new AliCodingRuntimeBinding(null, null, stop);
                    executorIdentity = StopExecutorIdentity(stop);
                    AddRuntimeAssets(runtimeBinding, executionAssets);
                }
                break;
            }
            case AliCodingInvocationKind.DotNetBuild:
            {
                targetPath = RequireString(arguments, "projectPath");
                var target = _dotNetResolver.ResolveExistingTarget(targetPath);
                physicalTarget = target.PhysicalPath;
                targetRoot = target.RootDirectory;
                executorIdentity = BindDotNetHostExecutor(
                    executionAssets,
                    out runtimeBinding);
                exactArguments["projectPath"] = targetPath;
                exactArguments["configuration"] = OptionalString(arguments, "configuration")
                    ?? "<null>";
                break;
            }
            case AliCodingInvocationKind.DotNetTest:
            case AliCodingInvocationKind.DotNetVerify:
            {
                targetPath = RequireString(arguments, "targetPath");
                var target = _dotNetResolver.ResolveExistingTarget(targetPath);
                physicalTarget = target.PhysicalPath;
                targetRoot = target.RootDirectory;
                var host = BindCurrentDotNetHost(executionAssets);
                runtimeBinding = new AliCodingRuntimeBinding(host, null, null);
                executorIdentity = "dotnet-sdk:" + ExecutionFileIdentity(host);
                exactArguments["targetPath"] = targetPath;
                exactArguments["configuration"] = OptionalString(arguments, "configuration")
                    ?? "<null>";
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        exactArguments["physicalTarget"] = NormalizePath(physicalTarget);
        exactArguments["targetRoot"] = NormalizePath(targetRoot);
        exactArguments["executor"] = executorIdentity;
        var targetRootIdentity = AliExecutionDirectoryBinding.CaptureExistingAncestor(
            targetRoot,
            "The selected coding source root spine");
        targetRootIdentity.AddTo(exactArguments, "targetRootSpine");
        runtimeBinding.AddTo(executionState);
        executionState["executor.identity"] = executorIdentity;
        foreach (var asset in executionAssets.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            executionState["asset." + HashText(asset.Key)] =
                asset.Key + "\0" + asset.Value;
        }
        foreach (var item in executionState.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            exactArguments["execution." + item.Key] = item.Value;
        }
        var targetState = AddExecutionState(
            AliCodingInputFingerprint.Capture(kind, physicalTarget, targetRoot),
            executionState);
        return new AliCodingInvocationBinding(
            kind,
            toolName,
            commandIdentity,
            executorIdentity,
            NormalizePath(targetRoot),
            targetRootIdentity,
            RootBinding([targetRoot]),
            WorkIdentityCanonicalizer.MapDigest(
                "coding-exact-invocation-binding-v1",
                exactArguments),
            targetState,
            runtimeBinding,
            executionAssets.ToFrozenDictionary(executionAssets.Comparer));
    }

    internal static void RequireProviderExecutionCanBeBound(
        AliCodingInvocationKind kind,
        string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        if (string.Equals(providerId, "cpp-msvc", StringComparison.Ordinal)
            && kind is AliCodingInvocationKind.ProviderTest
                or AliCodingInvocationKind.ProviderRun)
        {
            throw new NotSupportedException(
                "The selected C++ test/run operation would execute a derived binary that does "
                + "not exist at durable authorization time, so exact execution binding is unavailable.");
        }
    }

    internal static string TargetVersionDigest(TargetStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return WorkIdentityCanonicalizer.MapDigest(
            "action-target-versions-v1",
            snapshot.TargetVersions);
    }

    private static ProviderExecutionIdentity ProviderIdentity(
        AliCodingInvocationKind kind,
        IAliLanguageProvider provider,
        AliResolvedLanguageProject project,
        IDictionary<string, string> executionState,
        IDictionary<string, string> executionAssets)
    {
        var identity = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["provider"] = provider.Id
        };
        BindRequiredAsset(
            "provider.assembly",
            provider.GetType().Assembly.Location,
            "The selected coding provider assembly",
            identity,
            executionAssets);
        AliBoundExecutionFile? dotNetHost = null;
        var toolchains = provider.InspectToolchains(project)
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Executable, StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < toolchains.Length; index++)
        {
            var toolchain = toolchains[index];
            var prefix = $"provider.toolchain.{index:D3}";
            identity[prefix + ".name"] = toolchain.Name;
            identity[prefix + ".available"] = toolchain.Available.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            identity[prefix + ".purpose"] = toolchain.Purpose;
            if (!toolchain.Available)
            {
                identity[prefix + ".path"] = toolchain.Executable ?? "<null>";
                identity[prefix + ".identity"] = "absent";
                if (!string.IsNullOrWhiteSpace(toolchain.Executable)
                    && Path.IsPathFullyQualified(toolchain.Executable))
                {
                    executionAssets[NormalizePath(toolchain.Executable)] = "absent";
                }
                continue;
            }
            if (string.IsNullOrWhiteSpace(toolchain.Executable))
            {
                throw new InvalidDataException(
                    "An available coding toolchain has no exact executable or asset path.");
            }
            var path = Path.IsPathFullyQualified(toolchain.Executable)
                ? Path.GetFullPath(toolchain.Executable)
                : AliCodingExecutionAssetFingerprint.ResolveRequiredExecutable(
                    toolchain.Executable);
            var boundToolchain = BindRequiredAsset(
                prefix,
                path,
                "A selected coding provider toolchain",
                identity,
                executionAssets);
            if (string.Equals(toolchain.Name, "dotnet", StringComparison.Ordinal))
            {
                dotNetHost = boundToolchain;
            }
        }

        BindProviderSpecificAssets(
            kind,
            provider.Id,
            project,
            toolchains,
            identity,
            executionAssets);
        foreach (var item in identity)
        {
            executionState[item.Key] = item.Value;
        }
        return new ProviderExecutionIdentity(
            provider.Id + ":" + WorkIdentityCanonicalizer.MapDigest(
                "coding-provider-execution-assets-v1",
                identity),
            dotNetHost);
    }

    private static void BindProviderSpecificAssets(
        AliCodingInvocationKind kind,
        string providerId,
        AliResolvedLanguageProject project,
        IReadOnlyList<AliLanguageToolchainStatus> toolchains,
        IDictionary<string, string> identity,
        IDictionary<string, string> executionAssets)
    {
        if (string.Equals(providerId, "web-node", StringComparison.Ordinal))
        {
            var modules = Path.Combine(project.ProjectDirectory, "node_modules");
            if (kind is AliCodingInvocationKind.ProviderAnalyze
                or AliCodingInvocationKind.ProviderBuild)
            {
                BindOptionalAsset(
                    "provider.web.typescript-package",
                    Path.Combine(modules, "typescript"),
                    identity,
                    executionAssets);
            }
            if (kind == AliCodingInvocationKind.ProviderFormat)
            {
                BindOptionalAsset(
                    "provider.web.prettier-package",
                    Path.Combine(modules, "prettier"),
                    identity,
                    executionAssets);
            }
            BindOptionalAsset(
                "provider.web.bin-shims",
                Path.Combine(modules, ".bin"),
                identity,
                executionAssets);
            var scriptName = kind switch
            {
                AliCodingInvocationKind.ProviderBuild => "build",
                AliCodingInvocationKind.ProviderTest => "test",
                AliCodingInvocationKind.ProviderRun => "start",
                _ => null
            };
            if (scriptName is not null)
            {
                BindPackageScript(
                    project.ProjectDirectory,
                    scriptName,
                    identity,
                    executionAssets);
                var node = toolchains.FirstOrDefault(item =>
                    string.Equals(item.Name, "node", StringComparison.Ordinal)
                    && item.Available)?.Executable;
                if (!string.IsNullOrWhiteSpace(node))
                {
                    BindOptionalAsset(
                        "provider.web.npm-cli",
                        Path.Combine(
                            Path.GetDirectoryName(Path.GetFullPath(node))!,
                            "node_modules",
                            "npm",
                            "bin",
                            "npm-cli.js"),
                        identity,
                        executionAssets);
                }
            }
        }
        else if (string.Equals(providerId, "python-local", StringComparison.Ordinal)
                 || providerId.Contains("python", StringComparison.OrdinalIgnoreCase))
        {
            BindOptionalAsset(
                "provider.python.analyzer.primary",
                Path.Combine(
                    AppContext.BaseDirectory,
                    "Modules",
                    "Coding",
                    "Python",
                    "Tools",
                    "ali_python_analyzer.py"),
                identity,
                executionAssets);
            BindOptionalAsset(
                "provider.python.analyzer.fallback",
                Path.Combine(
                    AppContext.BaseDirectory,
                    "dependencies",
                    "coding",
                    "python",
                    "ali_python_analyzer.py"),
                identity,
                executionAssets);
            BindOptionalAsset(
                "provider.python.coding-packages",
                Path.Combine(
                    AppContext.BaseDirectory,
                    "runtime",
                    "python-coding-packages"),
                identity,
                executionAssets);
        }
        else if (string.Equals(providerId, "java-local", StringComparison.Ordinal)
                 || providerId.Contains("java", StringComparison.OrdinalIgnoreCase))
        {
            BindOptionalAsset(
                "provider.java.formatter",
                Path.Combine(
                    project.ProjectDirectory,
                    ".ali",
                    "tools",
                    "google-java-format.jar"),
                identity,
                executionAssets);
        }
    }

    private static void BindPackageScript(
        string projectRoot,
        string scriptName,
        IDictionary<string, string> identity,
        IDictionary<string, string> executionAssets)
    {
        const long maximumManifestBytes = 4L * 1024 * 1024;
        var manifest = Path.Combine(projectRoot, "package.json");
        if (!File.Exists(manifest))
        {
            identity["provider.web.npm-script." + scriptName] = "absent";
            return;
        }
        var boundManifest = BindRequiredAsset(
            "provider.web.package-manifest",
            manifest,
            "The selected web package manifest",
            identity,
            executionAssets);
        using var stream = AliCodingExecutionAssetFingerprint.OpenRegularFileNoFollow(
            manifest,
            "The selected web package manifest is not a regular local file.");
        if (stream.Length > maximumManifestBytes)
        {
            throw new InvalidDataException(
                "The selected web package manifest exceeds the fixed 4 MiB bound.");
        }
        using var document = JsonDocument.Parse(stream);
        var stableManifest = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            manifest,
            "The selected web package manifest");
        if (stableManifest != boundManifest)
        {
            throw new IOException(
                "The selected web package manifest changed while its exact script was read.");
        }
        identity["provider.web.npm-script." + scriptName] =
            document.RootElement.TryGetProperty("scripts", out var scripts)
            && scripts.ValueKind == JsonValueKind.Object
            && scripts.TryGetProperty(scriptName, out var script)
            && script.ValueKind == JsonValueKind.String
                ? script.GetString() ?? string.Empty
                : "absent";
    }

    private static AliBoundExecutionFile BindRequiredAsset(
        string key,
        string path,
        string description,
        IDictionary<string, string> identity,
        IDictionary<string, string> executionAssets)
    {
        var normalized = NormalizePath(path);
        var bound = File.Exists(path)
            ? AliCodingExecutionAssetFingerprint.CaptureRequiredFile(path, description)
            : new AliBoundExecutionFile(
                normalized,
                AliCodingExecutionAssetFingerprint.CaptureRequiredAsset(path, description));
        var assetIdentity = bound.Identity;
        identity[key + ".path"] = normalized;
        identity[key + ".identity"] = assetIdentity;
        executionAssets[normalized] = assetIdentity;
        return bound;
    }

    private static void BindOptionalAsset(
        string key,
        string path,
        IDictionary<string, string> identity,
        IDictionary<string, string> executionAssets)
    {
        var normalized = NormalizePath(path);
        var assetIdentity = File.Exists(path) || Directory.Exists(path)
            ? AliCodingExecutionAssetFingerprint.CaptureRequiredAsset(
                path,
                "A selected project-local coding tool asset")
            : "absent";
        identity[key + ".path"] = normalized;
        identity[key + ".identity"] = assetIdentity;
        executionAssets[normalized] = assetIdentity;
    }

    private static string DotNetExecutorIdentity(
        AliCodingInvocationKind kind,
        IDictionary<string, string> executionAssets) => kind switch
    {
        AliCodingInvocationKind.RoslynFormat =>
            "roslyn:" + AssemblyIdentity(
                typeof(AliRoslynCodingTools).Assembly.Location,
                executionAssets),
        AliCodingInvocationKind.DependencyApply =>
            "ali-xml-package-reference:" + AssemblyIdentity(
                typeof(AliCodingInvocationBindingResolver).Assembly.Location,
                executionAssets),
        _ => "dotnet-sdk:" + ExecutionFileIdentity(BindCurrentDotNetHost(executionAssets))
    };

    private static AliBoundExecutionFile BindCurrentDotNetHost(
        IDictionary<string, string> executionAssets)
    {
        var host = AliExactDotNetHost.CaptureCurrent();
        executionAssets[host.PhysicalPath] = host.Identity;
        return host;
    }

    private static string BindDotNetHostExecutor(
        IDictionary<string, string> executionAssets,
        out AliCodingRuntimeBinding runtimeBinding)
    {
        var host = BindCurrentDotNetHost(executionAssets);
        runtimeBinding = new AliCodingRuntimeBinding(host, null, null);
        return "dotnet-sdk:" + ExecutionFileIdentity(host);
    }

    private static string ExecutionFileIdentity(AliBoundExecutionFile file) =>
        HashText(file.PhysicalPath + "\0" + file.Identity);

    private static string AssemblyIdentity(
        string path,
        IDictionary<string, string> executionAssets)
    {
        var file = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            path,
            "A coding executor identity");
        executionAssets[file.PhysicalPath] = file.Identity;
        return HashText(file.PhysicalPath + "\0" + file.Identity);
    }

    private static string RunExecutorIdentity(AliDotNetRunExecutionBinding run) =>
        run.Artifact is null
            ? "dotnet-run:no-built-artifact"
            : "dotnet-run:" + HashText(string.Join(
                "\0",
                run.HostExecutable?.PhysicalPath
                    ?? throw new InvalidDataException(
                        "A selected .NET run artifact has no exact host executable path."),
                run.HostExecutable.Identity,
                run.LaunchClosure?.OutputDirectoryPath
                    ?? throw new InvalidDataException(
                        "A selected .NET run artifact has no exact launch-output path."),
                run.LaunchClosure.Identity));

    private static string StopExecutorIdentity(AliDotNetStopExecutionBinding stop) =>
        stop.Process is null
            ? "dotnet-stop:no-running-process"
            : "dotnet-stop:" + HashText(string.Join(
                "\0",
                stop.Process.ProcessId,
                stop.Process.StartTimeUtcTicks,
                stop.Process.Executable.PhysicalPath,
                stop.Process.Executable.Identity));

    private static void AddRuntimeAssets(
        AliCodingRuntimeBinding runtime,
        IDictionary<string, string> executionAssets)
    {
        Add(runtime.DotNetHost);
        Add(runtime.DotNetRun?.Artifact);
        Add(runtime.DotNetRun?.HostExecutable);
        if (runtime.DotNetRun?.LaunchClosure is { } closure)
        {
            executionAssets[closure.OutputDirectoryPath] = closure.Identity;
        }
        Add(runtime.DotNetStop?.Artifact);
        Add(runtime.DotNetStop?.Process?.Executable);

        void Add(AliBoundExecutionFile? file)
        {
            if (file is not null)
            {
                executionAssets[file.PhysicalPath] = file.Identity;
            }
        }
    }

    private sealed record ProviderExecutionIdentity(
        string Identity,
        AliBoundExecutionFile? DotNetHost);

    private static TargetStateSnapshot AddExecutionState(
        TargetStateSnapshot source,
        IReadOnlyDictionary<string, string> executionState)
    {
        var versions = source.TargetVersions.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal);
        foreach (var item in executionState)
        {
            versions["coding-execution:" + item.Key] = item.Value;
        }
        var frozen = versions.ToFrozenDictionary(StringComparer.Ordinal);
        return source with
        {
            TargetVersions = frozen,
            ArtifactVersions = frozen
        };
    }

    private static string RootBinding(IEnumerable<string> roots)
    {
        var normalized = roots
            .Select(NormalizePath)
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select((path, index) => new KeyValuePair<string, string>(
                index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                path))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        return WorkIdentityCanonicalizer.MapDigest(
            "ali-coding-root-binding-v2",
            normalized);
    }

    private static string RequireString(JsonElement arguments, string propertyName)
    {
        var value = OptionalString(arguments, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"The exact '{propertyName}' coding argument is required.");
        }
        return value;
    }

    private static string? OptionalString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var value))
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"The exact '{propertyName}' coding argument must be a string.");
        }
        return value.GetString();
    }

    private static string NormalizePath(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

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

internal sealed class AliCodingProcessTargetStateAdapter : IActionTargetStateAdapter
{
    private static readonly FrozenDictionary<string, AliCodingInvocationKind> KindsByTool =
        AliCodingInvocationCatalog.All.ToFrozenDictionary(
            AliCodingInvocationCatalog.ToolName,
            kind => kind,
            StringComparer.Ordinal);
    private readonly AliCodingInvocationBindingResolver _bindings;

    internal AliCodingProcessTargetStateAdapter(AliCodingInvocationBindingResolver bindings)
    {
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
    }

    public IReadOnlyCollection<string> ToolNames { get; } =
        Array.AsReadOnly(AliCodingInvocationCatalog.All
            .Select(AliCodingInvocationCatalog.ToolName)
            .Order(StringComparer.Ordinal)
            .ToArray());

    public TargetStateSnapshot Capture(string toolName, JsonElement arguments)
    {
        if (!KindsByTool.TryGetValue(toolName, out var kind))
        {
            throw new InvalidOperationException(
                "The coding target-state adapter has no exact registration for this tool.");
        }
        return _bindings.Resolve(kind, arguments).TargetState;
    }
}

/// <summary>
/// Bounded, no-follow aggregate of the exact ordinary project tree presented to a coding tool.
/// Fixed generated/cache directories are excluded because they are outputs or tool installations,
/// not canonical source inputs. Oversized or linked trees fail closed instead of weakening the
/// accepted target identity.
/// </summary>
internal static class AliCodingInputFingerprint
{
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
        AliCodingInvocationKind kind,
        string physicalTarget,
        string targetRoot)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (kind == AliCodingInvocationKind.DotNetCreate)
        {
            var count = 0;
            long bytes = 0;
            versions["coding-create-target-v1"] = File.Exists(physicalTarget)
                ? "file:" + HashFile(physicalTarget, ref count, ref bytes)
                : Directory.Exists(targetRoot)
                    ? "directory:" + CaptureTree(targetRoot)
                    : "absent";
        }
        else
        {
            var count = 0;
            long bytes = 0;
            versions["coding-target-file-v1"] = HashFile(
                physicalTarget,
                ref count,
                ref bytes);
            versions["coding-input-tree-v1"] = CaptureTree(targetRoot);
        }
        versions["coding-generated-output-layout-v1"] =
            AliGeneratedOutputLayoutFingerprint.Capture(targetRoot);

        var frozen = versions.ToFrozenDictionary(StringComparer.Ordinal);
        return new TargetStateSnapshot(
            frozen,
            frozen,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    internal static string CaptureTree(string root)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        AliCodingExecutionAssetFingerprint.ValidateRegularDirectoryNoFollow(
            canonicalRoot,
            "A coding input root is not a regular local directory.");
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pending = new Stack<string>();
        pending.Push(canonicalRoot);
        var count = 0;
        long bytes = 0;

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            AliCodingExecutionAssetFingerprint.ValidateRegularDirectoryNoFollow(
                directory,
                "A coding input root is not a regular local directory.");
            var entries = Directory.EnumerateFileSystemEntries(directory)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var childDirectories = new List<string>();
            foreach (var entry in entries)
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new InvalidDataException(
                        "A coding input tree contains a reparse point or device entry.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!ExcludedDirectoryNames.Contains(Path.GetFileName(entry)))
                    {
                        childDirectories.Add(entry);
                    }
                    continue;
                }

                count = checked(count + 1);
                if (count > MaximumFiles)
                {
                    throw new InvalidDataException(
                        "The coding input tree exceeds its fixed file-count bound.");
                }
                var relative = Path.GetRelativePath(canonicalRoot, entry)
                    .Replace('\\', '/');
                Append(aggregate, relative);
                Append(aggregate, HashFile(entry, ref count, ref bytes, countAlreadyIncluded: true));
            }

            for (var index = childDirectories.Count - 1; index >= 0; index--)
            {
                pending.Push(childDirectories[index]);
            }
        }

        Append(aggregate, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(aggregate, bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
        ref int count,
        ref long aggregateBytes,
        bool countAlreadyIncluded = false)
    {
        if (!countAlreadyIncluded)
        {
            count = checked(count + 1);
            if (count > MaximumFiles)
            {
                throw new InvalidDataException(
                    "The coding input tree exceeds its fixed file-count bound.");
            }
        }
        using var stream = AliCodingExecutionAssetFingerprint.OpenRegularFileNoFollow(
            Path.GetFullPath(path),
            "A coding input is not a regular local file.");
        var length = stream.Length;
        if (length < 0 || length > MaximumFileBytes)
        {
            throw new InvalidDataException(
                "A coding input file exceeds its fixed size bound.");
        }
        aggregateBytes = checked(aggregateBytes + length);
        if (aggregateBytes > MaximumAggregateBytes)
        {
            throw new InvalidDataException(
                "The coding input tree exceeds its fixed aggregate size bound.");
        }
        var hash = SHA256.HashData(stream);
        try
        {
            if (stream.Position != length || stream.Length != length)
            {
                throw new InvalidDataException(
                    "A coding input changed while its exact hash was captured.");
            }
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
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
