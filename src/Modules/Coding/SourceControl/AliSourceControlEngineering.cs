using System.Text.RegularExpressions;
using Ali.Modules.Coding.Infrastructure;

namespace Ali.Modules.Coding.SourceControl;

public sealed record SourceControlResult(bool Success, string Operation, string RepositoryRoot, string Summary, string Output, int ExitCode);

/// <summary>Bounded Git porcelain for repositories containing an approved project; never accepts arbitrary arguments.</summary>
internal sealed partial class AliSourceControlEngineering(AliCodingProjectResolver resolver)
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex RefPattern();

    public Task<SourceControlResult> StatusAsync(string targetPath, CancellationToken cancellationToken) =>
        RunAsync(targetPath, "status", ["status", "--short", "--branch"], cancellationToken);

    public Task<SourceControlResult> DiffAsync(string targetPath, bool staged, CancellationToken cancellationToken) =>
        RunAsync(targetPath, "diff", staged ? ["diff", "--cached", "--stat", "--patch"] : ["diff", "--stat", "--patch"], cancellationToken);

    public Task<SourceControlResult> HistoryAsync(string targetPath, int count, CancellationToken cancellationToken)
    {
        var bounded = Math.Clamp(count, 1, 100);
        return RunAsync(targetPath, "history", ["log", $"-{bounded}", "--date=iso-strict", "--pretty=format:%H%x09%ad%x09%an%x09%s"], cancellationToken);
    }

    public Task<SourceControlResult> BlameAsync(string targetPath, string documentPath, CancellationToken cancellationToken)
    {
        var target = resolver.ResolveExistingTarget(targetPath);
        var document = resolver.ResolveDocument(target, documentPath);
        var repository = ResolveRepository(target);
        var relative = Path.GetRelativePath(repository, document);
        return RunRepositoryAsync(repository, "blame", ["blame", "--line-porcelain", "--", relative], cancellationToken);
    }

    public Task<SourceControlResult> CreateBranchAsync(string targetPath, string branchName, CancellationToken cancellationToken)
    {
        ValidateRef(branchName);
        return RunAsync(targetPath, "create-branch", ["switch", "-c", branchName], cancellationToken);
    }

    public Task<SourceControlResult> CommitAsync(string targetPath, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 200 || message.ContainsAny('\r', '\n'))
            throw new ArgumentException("Commit message must be one non-empty line of at most 200 characters.", nameof(message));
        return RunAsync(targetPath, "commit", ["commit", "-m", message], cancellationToken);
    }

    public Task<SourceControlResult> PushAsync(string targetPath, string remote, string branchName, CancellationToken cancellationToken)
    {
        ValidateRef(remote);
        ValidateRef(branchName);
        return RunAsync(targetPath, "push", ["push", remote, branchName], cancellationToken);
    }

    private async Task<SourceControlResult> RunAsync(string targetPath, string operation, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var target = resolver.ResolveExistingTarget(targetPath);
        return await RunRepositoryAsync(ResolveRepository(target), operation, arguments, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SourceControlResult> RunRepositoryAsync(string repository, string operation, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await AliBoundedProcessRunner.RunAsync("git", repository, arguments, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        return new SourceControlResult(result.Success, operation, repository,
            result.Success ? $"Git {operation} completed." : $"Git {operation} failed or timed out.", result.Output, result.ExitCode);
    }

    private static string ResolveRepository(AliResolvedCodingTarget target)
    {
        var mount = Path.TrimEndingDirectorySeparator(target.MountRoot);
        var current = new DirectoryInfo(target.RootDirectory);
        while (current is not null && current.FullName.StartsWith(mount, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("No Git repository contains the approved coding target.");
    }

    private static void ValidateRef(string value)
    {
        if (!RefPattern().IsMatch(value) || value.Contains("..", StringComparison.Ordinal) || value.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Git remote or branch name is invalid.");
    }
}

file static class CharacterSearchExtensions
{
    public static bool ContainsAny(this string value, params char[] characters) => value.IndexOfAny(characters) >= 0;
}
