using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Capabilities;

/// <summary>
/// Supplies the stable capability identity for Ali's non-disableable planning protocol.
/// The planning client replaces this invariant inventory schema with a per-pass schema
/// containing only the currently eligible task actions and exact tool arguments.
/// </summary>
public static class OrchestrationProtocolCapability
{
    public const string ToolName = "submit_orchestration_decision";

    public static AIFunction CreateInvariantFunction() =>
        AIFunctionFactory.Create(
            (Func<JsonElement, string>)(_ => throw new InvalidOperationException(
                "The orchestration protocol can be consumed only by Ali's planning client.")),
            ToolName,
            "Submit one typed orchestration transition. This protocol is always available and cannot be disabled with task capability settings.");
}
