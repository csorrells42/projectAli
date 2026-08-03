using System.Text.Json;
using Microsoft.Agents.AI;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Decorates Agent Framework skills at their typed source/resource/script boundaries.
/// Model-facing strings are never interpreted as outcome evidence.
/// </summary>
internal sealed class AliOutcomeReportingAgentSkillsSource(
    AgentSkillsSource inner,
    AliFrameworkToolOutcomeSidecar outcomes) : AgentSkillsSource
{
    private readonly AgentSkillsSource _inner = inner
        ?? throw new ArgumentNullException(nameof(inner));
    private readonly AliFrameworkToolOutcomeSidecar _outcomes = outcomes
        ?? throw new ArgumentNullException(nameof(outcomes));
    private readonly object _inventoryObserverSync = new();
    private Action<AgentSession?, IReadOnlySet<string>?>? _inventoryObserver;

    public override async Task<IList<AgentSkill>> GetSkillsAsync(
        AgentSkillsSourceContext context,
        CancellationToken cancellationToken)
    {
        ReportInventory(context.Session, skillNames: null);
        try
        {
            var skills = await _inner.GetSkillsAsync(context, cancellationToken).ConfigureAwait(false);
            var wrapped = skills
                .Select(skill => WrapSkill(skill, _outcomes))
                .ToArray();
            ReportInventory(
                context.Session,
                new HashSet<string>(
                    wrapped.Select(skill => skill.Frontmatter.Name),
                    StringComparer.Ordinal));
            return wrapped;
        }
        catch
        {
            ReportInventory(context.Session, skillNames: null);
            throw;
        }
    }

    internal void ConfigureInventoryReporting(
        Action<AgentSession?, IReadOnlySet<string>?> inventoryObserver)
    {
        ArgumentNullException.ThrowIfNull(inventoryObserver);
        lock (_inventoryObserverSync)
        {
            if (_inventoryObserver is not null)
            {
                throw new InvalidOperationException(
                    "Agent skill inventory outcome reporting was already configured.");
            }

            _inventoryObserver = inventoryObserver;
        }
    }

    internal static AgentSkill WrapSkill(
        AgentSkill skill,
        AliFrameworkToolOutcomeSidecar outcomes)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(outcomes);
        return new OutcomeReportingSkill(skill, outcomes);
    }

    private void ReportInventory(
        AgentSession? session,
        IReadOnlySet<string>? skillNames)
    {
        Action<AgentSession?, IReadOnlySet<string>?>? observer;
        lock (_inventoryObserverSync)
        {
            observer = _inventoryObserver;
        }

        try
        {
            observer?.Invoke(session, skillNames);
        }
        catch
        {
            // Inventory observation cannot alter source discovery. Missing inventory
            // leaves provider early-return cases fail-closed as Unreported.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class OutcomeReportingSkill(
        AgentSkill innerSkill,
        AliFrameworkToolOutcomeSidecar outcomes) : AgentSkill
    {
        private readonly AgentSkill _inner = innerSkill
            ?? throw new ArgumentNullException(nameof(innerSkill));

        public override AgentSkillFrontmatter Frontmatter => _inner.Frontmatter;

        public override async ValueTask<string> GetContentAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                var content = await _inner.GetContentAsync(cancellationToken).ConfigureAwait(false);
                Report(
                    [AliCapabilityCatalog.LoadAgentSkillName],
                    content is null
                        ? AliFrameworkToolOutcomeSignal.Failed
                        : AliFrameworkToolOutcomeSignal.Completed);
                // Preserve the inner provider's return semantics even if a custom
                // skill violates its non-null contract; the sidecar already records
                // that exact boundary violation as Failed.
                return content!;
            }
            catch
            {
                Report(
                    [AliCapabilityCatalog.LoadAgentSkillName],
                    AliFrameworkToolOutcomeSignal.Failed);
                throw;
            }
        }

        public override async ValueTask<AgentSkillResource?> GetResourceAsync(
            string name,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var resource = await _inner.GetResourceAsync(name, cancellationToken)
                    .ConfigureAwait(false);
                if (resource is null)
                {
                    Report(
                        [AliCapabilityCatalog.ReadAgentSkillResourceName],
                        AliFrameworkToolOutcomeSignal.NotFound);
                    return null;
                }

                return new OutcomeReportingResource(resource, outcomes);
            }
            catch
            {
                Report(
                    [AliCapabilityCatalog.ReadAgentSkillResourceName],
                    AliFrameworkToolOutcomeSignal.Failed);
                throw;
            }
        }

        public override async ValueTask<AgentSkillScript?> GetScriptAsync(
            string name,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var script = await _inner.GetScriptAsync(name, cancellationToken)
                    .ConfigureAwait(false);
                if (script is null)
                {
                    Report(
                        [AliCapabilityCatalog.RunAgentSkillScriptName],
                        AliFrameworkToolOutcomeSignal.NotFound);
                    return null;
                }

                return new OutcomeReportingScript(script, _inner, outcomes);
            }
            catch
            {
                Report(
                    [AliCapabilityCatalog.RunAgentSkillScriptName],
                    AliFrameworkToolOutcomeSignal.Failed);
                throw;
            }
        }

        private void Report(
            IReadOnlyList<string> toolNames,
            AliFrameworkToolOutcomeSignal signal)
        {
            try
            {
                outcomes.TryRecordActive(toolNames, signal);
            }
            catch
            {
                // Observation cannot change the skill provider's behavior.
            }
        }
    }

    private sealed class OutcomeReportingResource(
        AgentSkillResource innerResource,
        AliFrameworkToolOutcomeSidecar outcomes) :
        AgentSkillResource(innerResource.Name, innerResource.Description)
    {
        private readonly AgentSkillResource _inner = innerResource;

        public override async Task<object?> ReadAsync(
            IServiceProvider? serviceProvider,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await _inner.ReadAsync(serviceProvider, cancellationToken)
                    .ConfigureAwait(false);
                Report(AliFrameworkToolOutcomeSignal.Found);
                return result;
            }
            catch
            {
                Report(AliFrameworkToolOutcomeSignal.Failed);
                throw;
            }
        }

        private void Report(AliFrameworkToolOutcomeSignal signal)
        {
            try
            {
                outcomes.TryRecordActive(
                    [AliCapabilityCatalog.ReadAgentSkillResourceName],
                    signal);
            }
            catch
            {
                // Observation cannot change resource-read behavior.
            }
        }
    }

    private sealed class OutcomeReportingScript(
        AgentSkillScript innerScript,
        AgentSkill ownerSkill,
        AliFrameworkToolOutcomeSidecar outcomes) :
        AgentSkillScript(innerScript.Name, innerScript.Description)
    {
        private readonly AgentSkillScript _inner = innerScript;
        private readonly AgentSkill _owner = ownerSkill;

        public override JsonElement? ParametersSchema => _inner.ParametersSchema;

        public override async Task<object?> RunAsync(
            AgentSkill skill,
            JsonElement? arguments,
            IServiceProvider? serviceProvider,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await _inner.RunAsync(
                        _owner,
                        arguments,
                        serviceProvider,
                        cancellationToken)
                    .ConfigureAwait(false);
                Report(AliFrameworkToolOutcomeSignal.Completed);
                return result;
            }
            catch
            {
                Report(AliFrameworkToolOutcomeSignal.Failed);
                throw;
            }
        }

        private void Report(AliFrameworkToolOutcomeSignal signal)
        {
            try
            {
                outcomes.TryRecordActive(
                    [AliCapabilityCatalog.RunAgentSkillScriptName],
                    signal);
            }
            catch
            {
                // Observation cannot change script execution behavior.
            }
        }
    }
}
