using System.Text.RegularExpressions;
using Ali.Modules.Serena;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Serena maintains its own machine-global project registry (a plain list of
/// every path it has ever activated, anywhere on the machine), entirely
/// independent of Ali's own configured Workspace root. A name collision --
/// confirmed live, not hypothetical -- let the model activate a same-named
/// project outside Ali's sandbox by calling activate_project with a bare
/// name. Cleaning up the stray registry entry fixes that one instance; this
/// middleware is the durable fix: after every activate_project call, the
/// resolved project path Serena reports is checked against the configured
/// Workspace root, and if it falls outside it, the result is replaced with an
/// explicit rejection instead of letting the model continue to operate there.
/// </summary>
internal static partial class AliSerenaWorkspaceGuardMiddleware
{
    [GeneratedRegex(
        @"(?:with name '[^']*'|created and activated a new project with name '[^']*')\s+at\s+(?<path>.+?)\s+is activated",
        RegexOptions.IgnoreCase)]
    private static partial Regex ActivationMessagePattern();

    internal const string ActivateProjectToolName = "activate_project";

    internal static AIAgent WithWorkspaceGuard(AIAgent agent, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var normalizedRoot = NormalizeForComparison(Path.GetFullPath(workspaceRoot));

        var builder = new AIAgentBuilder(agent);
        builder.Use(async (
            AIAgent _,
            FunctionInvocationContext context,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
            CancellationToken cancellationToken) =>
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(context.Function.Name, ActivateProjectToolName, StringComparison.Ordinal)
                || result is not string resultText
                || string.IsNullOrWhiteSpace(resultText))
            {
                return result;
            }

            var match = ActivationMessagePattern().Match(resultText);
            if (!match.Success)
            {
                // The resolved path could not be confidently identified from
                // Serena's own message. Fail closed rather than trust an
                // activation whose scope cannot be verified.
                return "Rejected: the activated project's location could not be verified against the configured Workspace root. Do not proceed with file or command operations until this is resolved.";
            }

            var resolvedPath = NormalizeForComparison(match.Groups["path"].Value.Trim());
            if (!resolvedPath.StartsWith(normalizedRoot, StringComparison.Ordinal))
            {
                return $"Rejected: the activated project at \"{match.Groups["path"].Value.Trim()}\" is outside the configured Workspace root and cannot be used. Reactivate only a project located inside the Workspace, and never call activate_project again this turn to navigate into a subfolder -- use ordinary relative paths with your other tools instead.";
            }

            return result;
        });
        return builder.Build();
    }

    private static string NormalizeForComparison(string path) =>
        path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
}
