using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.WorkstationFiles;

namespace Ali.Modules.Coding.SourceControl;

internal enum AliGitInvocationKind
{
    Status,
    Diff,
    CreateBranch,
    Commit,
    Push
}

/// <summary>
/// Closed identities for the five registered Git schemas. These recipes describe an already
/// selected production operation; they never select a command from user text or an executable.
/// </summary>
internal static class AliGitInvocationCatalog
{
    internal static IReadOnlyList<AliGitInvocationKind> All { get; } =
        Array.AsReadOnly(new[]
        {
            AliGitInvocationKind.Status,
            AliGitInvocationKind.Diff,
            AliGitInvocationKind.CreateBranch,
            AliGitInvocationKind.Commit,
            AliGitInvocationKind.Push
        });

    internal static string ToolName(AliGitInvocationKind kind) => kind switch
    {
        AliGitInvocationKind.Status => AliCapabilityCatalog.GitStatusName,
        AliGitInvocationKind.Diff => AliCapabilityCatalog.GitDiffName,
        AliGitInvocationKind.CreateBranch => AliCapabilityCatalog.GitCreateBranchName,
        AliGitInvocationKind.Commit => AliCapabilityCatalog.GitCommitName,
        AliGitInvocationKind.Push => AliCapabilityCatalog.GitPushName,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static string ProviderOperation(AliGitInvocationKind kind) => kind switch
    {
        AliGitInvocationKind.Status => "status",
        AliGitInvocationKind.Diff => "diff",
        AliGitInvocationKind.CreateBranch => "create-branch",
        AliGitInvocationKind.Commit => "commit",
        AliGitInvocationKind.Push => "push",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static string CommandIdentity(AliGitInvocationKind kind) => kind switch
    {
        AliGitInvocationKind.Status => "ali.git.status-short-branch.v1",
        AliGitInvocationKind.Diff => "ali.git.diff-stat-patch.v1",
        AliGitInvocationKind.CreateBranch => "ali.git.switch-create.v1",
        AliGitInvocationKind.Commit => "ali.git.commit-message.v1",
        AliGitInvocationKind.Push => "ali.git.push-exact-ref.v1",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static string Recipe(AliGitInvocationKind kind, bool staged = false) => kind switch
    {
        AliGitInvocationKind.Status => "status\0--short\0--branch",
        AliGitInvocationKind.Diff when staged =>
            "diff\0--cached\0--no-ext-diff\0--no-textconv\0--stat\0--patch",
        AliGitInvocationKind.Diff =>
            "diff\0--no-ext-diff\0--no-textconv\0--stat\0--patch",
        AliGitInvocationKind.CreateBranch => "switch\0-c\0<branchName>",
        AliGitInvocationKind.Commit => "commit\0-m\0<message>",
        AliGitInvocationKind.Push => "push\0<remote>\0<branchName>",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static TimeSpan ExecutionTimeout(AliGitInvocationKind kind) => kind switch
    {
        AliGitInvocationKind.Status => TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(10),
        AliGitInvocationKind.Diff => TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(10),
        AliGitInvocationKind.CreateBranch => TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(10),
        AliGitInvocationKind.Commit => TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(10),
        AliGitInvocationKind.Push => TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(10),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static string EffectKind(AliGitInvocationKind kind) => kind switch
    {
        AliGitInvocationKind.Status or AliGitInvocationKind.Diff => "read",
        AliGitInvocationKind.CreateBranch or AliGitInvocationKind.Commit => "update",
        AliGitInvocationKind.Push => "external",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

internal sealed record AliGitInvocationBinding(
    AliGitInvocationKind Kind,
    string ToolName,
    string CommandIdentity,
    string ProviderOperation,
    string ProviderIdentity,
    string RepositoryRoot,
    AliExecutionDirectoryBinding RepositoryRootIdentity,
    string RootBinding,
    string DomainPreparationDigest,
    TargetStateSnapshot TargetState,
    IReadOnlyList<AliGitExecutionFileBinding> ExecutionFiles);

/// <summary>
/// Resolves typed fields for one already-selected Git schema and binds them to one exact local
/// repository and one fixed provider operation. It has no generic command or argument surface.
/// </summary>
internal sealed class AliGitInvocationBindingResolver
{
    private readonly AliCodingProjectResolver _resolver;
    private readonly AliGitProviderPin _provider;

    internal AliGitInvocationBindingResolver(
        AliCodingProjectResolver resolver,
        AliGitProviderPin provider)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    internal AliGitInvocationBindingResolver(
        AliWorkstationFileAccess fileAccess,
        AliCodingProjectResolver resolver,
        AliGitProviderPin provider)
        : this(resolver, provider)
    {
        ArgumentNullException.ThrowIfNull(fileAccess);
    }

    internal AliGitInvocationBinding Resolve(
        AliGitInvocationKind kind,
        JsonElement arguments)
    {
        AliGitArgumentValidation.RequireExactSchema(kind, arguments);
        var targetPath = AliGitArgumentValidation.RequireString(arguments, "targetPath");
        var target = _resolver.ResolveExistingTarget(targetPath);
        var repository = AliGitRepositoryLayout.Resolve(target);
        var providerIdentity = _provider.CaptureIdentity();
        var staged = kind == AliGitInvocationKind.Diff
            && AliGitArgumentValidation.RequireBoolean(arguments, "staged");
        string? remote = null;
        var exactArguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tool"] = AliGitInvocationCatalog.ToolName(kind),
            ["providerOperation"] = AliGitInvocationCatalog.ProviderOperation(kind),
            ["commandIdentity"] = AliGitInvocationCatalog.CommandIdentity(kind),
            ["recipe"] = AliGitInvocationCatalog.Recipe(kind, staged),
            ["targetPath"] = targetPath,
            ["repositoryRoot"] = NormalizePath(repository.RepositoryRoot),
            ["gitDirectory"] = NormalizePath(repository.WorktreeGitDirectory),
            ["commonGitDirectory"] = NormalizePath(repository.CommonGitDirectory),
            ["providerIdentity"] = providerIdentity,
            ["providerPolicy"] = AliGitExecutionPolicy.PolicyIdentity,
            ["providerTimeoutMilliseconds"] = "120000",
            ["adapterTimeoutMilliseconds"] = checked((long)AliGitInvocationCatalog
                    .ExecutionTimeout(kind).TotalMilliseconds)
                .ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        switch (kind)
        {
            case AliGitInvocationKind.Status:
                break;
            case AliGitInvocationKind.Diff:
                exactArguments["staged"] = staged
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case AliGitInvocationKind.CreateBranch:
                exactArguments["branchName"] =
                    AliGitArgumentValidation.RequireRef(arguments, "branchName");
                break;
            case AliGitInvocationKind.Commit:
                exactArguments["message"] =
                    AliGitArgumentValidation.RequireCommitMessage(arguments);
                break;
            case AliGitInvocationKind.Push:
                remote = AliGitArgumentValidation.RequireRef(arguments, "remote");
                exactArguments["remote"] = remote;
                exactArguments["branchName"] =
                    AliGitArgumentValidation.RequireRef(arguments, "branchName");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var effectiveInputs = AliGitEffectiveInputCapture.Capture(
            repository,
            _provider,
            kind,
            remote);
        var repositoryRootIdentity = AliExecutionDirectoryBinding.Capture(
            repository.RepositoryRoot,
            "The selected Git repository root spine");
        repositoryRootIdentity.AddTo(exactArguments, "repositoryRootSpine");
        exactArguments["effectiveConfiguration"] = effectiveInputs.ConfigurationDigest;
        exactArguments["effectiveHelpers"] = effectiveInputs.HelperDigest;
        exactArguments["explicitRemote"] = effectiveInputs.RemoteDigest;
        var targetState = AliGitRepositoryStateCapture.Capture(
            repository,
            effectiveInputs);
        return new AliGitInvocationBinding(
            kind,
            AliGitInvocationCatalog.ToolName(kind),
            AliGitInvocationCatalog.CommandIdentity(kind),
            AliGitInvocationCatalog.ProviderOperation(kind),
            providerIdentity,
            repository.RepositoryRoot,
            repositoryRootIdentity,
            RootBinding(repository.RepositoryRoot),
            WorkIdentityCanonicalizer.MapDigest(
                "git-exact-invocation-binding-v2",
                exactArguments),
            targetState,
            effectiveInputs.ExecutionFiles);
    }

    internal static string TargetVersionDigest(TargetStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return WorkIdentityCanonicalizer.MapDigest(
            "action-target-versions-v1",
            snapshot.TargetVersions);
    }

    internal static string RootBinding(string repositoryRoot) =>
        HashText("ali-git-repository-root-v1\0" + NormalizePath(repositoryRoot));

    private static string NormalizePath(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}

internal sealed class AliGitTargetStateAdapter : IActionTargetStateAdapter
{
    private static readonly FrozenDictionary<string, AliGitInvocationKind> KindsByTool =
        AliGitInvocationCatalog.All.ToFrozenDictionary(
            AliGitInvocationCatalog.ToolName,
            kind => kind,
            StringComparer.Ordinal);
    private readonly AliGitInvocationBindingResolver _bindings;

    internal AliGitTargetStateAdapter(AliGitInvocationBindingResolver bindings)
    {
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
    }

    public IReadOnlyCollection<string> ToolNames { get; } =
        Array.AsReadOnly(AliGitInvocationCatalog.All
            .Select(AliGitInvocationCatalog.ToolName)
            .Order(StringComparer.Ordinal)
            .ToArray());

    public TargetStateSnapshot Capture(string toolName, JsonElement arguments)
    {
        if (!KindsByTool.TryGetValue(toolName, out var kind))
        {
            throw new InvalidOperationException(
                "The Git target-state adapter has no exact registration for this tool.");
        }
        return _bindings.Resolve(kind, arguments).TargetState;
    }
}

internal static partial class AliGitArgumentValidation
{
    private static readonly FrozenDictionary<AliGitInvocationKind, FrozenSet<string>> Schemas =
        new Dictionary<AliGitInvocationKind, FrozenSet<string>>
        {
            [AliGitInvocationKind.Status] = Set("targetPath"),
            [AliGitInvocationKind.Diff] = Set("targetPath", "staged"),
            [AliGitInvocationKind.CreateBranch] = Set("targetPath", "branchName"),
            [AliGitInvocationKind.Commit] = Set("targetPath", "message"),
            [AliGitInvocationKind.Push] = Set("targetPath", "remote", "branchName")
        }.ToFrozenDictionary();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex RefPattern();

    internal static void RequireExactSchema(
        AliGitInvocationKind kind,
        JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Git invocation arguments must be an object.");
        }
        if (!Schemas.TryGetValue(kind, out var expected))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!actual.Add(property.Name) || !expected.Contains(property.Name))
            {
                throw new InvalidDataException(
                    "The Git invocation contains a duplicate or unregistered argument.");
            }
        }
        if (!actual.SetEquals(expected))
        {
            throw new InvalidDataException(
                "The Git invocation does not match the exact registered argument schema.");
        }
    }

    internal static string RequireString(JsonElement arguments, string propertyName)
    {
        if (arguments.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()))
        {
            return property.GetString()!;
        }
        throw new InvalidDataException(
            $"The exact '{propertyName}' Git argument is required.");
    }

    internal static bool RequireBoolean(JsonElement arguments, string propertyName)
    {
        if (arguments.TryGetProperty(propertyName, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return property.GetBoolean();
        }
        throw new InvalidDataException(
            $"The exact '{propertyName}' Git argument must be Boolean.");
    }

    internal static string RequireRef(JsonElement arguments, string propertyName)
    {
        var value = RequireString(arguments, propertyName);
        if (!RefPattern().IsMatch(value)
            || value.Contains("..", StringComparison.Ordinal)
            || value.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The exact '{propertyName}' Git ref argument is invalid.");
        }
        return value;
    }

    internal static string RequireCommitMessage(JsonElement arguments)
    {
        var message = RequireString(arguments, "message");
        if (message.Length > 200 || message.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new InvalidDataException(
                "The exact Git commit message must be one line of at most 200 characters.");
        }
        return message;
    }

    private static FrozenSet<string> Set(params string[] names) =>
        names.ToFrozenSet(StringComparer.Ordinal);
}
