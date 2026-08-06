using System.Text.Json;
using Ali.Modules.Diagnostics;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Serena;

internal sealed class SerenaTransportDiagnosticsAIFunction(AIFunction innerFunction)
    : DelegatingAIFunction(innerFunction)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        AliTransportDiagnostics.RecordSerenaRequest(SerializeRequest(Name, arguments));
        var result = await InnerFunction.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        AliTransportDiagnostics.RecordSerenaResponse(SerializeResponse(Name, result));
        return result;
    }

    private static string SerializeRequest(string toolName, AIFunctionArguments arguments) =>
        JsonSerializer.Serialize(new { tool = toolName, arguments }, JsonOptions);

    private static string SerializeResponse(string toolName, object? result) =>
        JsonSerializer.Serialize(new { tool = toolName, result }, JsonOptions);
}
