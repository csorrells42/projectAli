using Ali.Modules.Coordinator;
using System.Text.Json;

namespace Ali.UI.ViewModels;

public sealed class AgentActivityItemViewModel
{
    public AgentActivityItemViewModel(AssistantStreamChunk chunk)
    {
        Kind = chunk.ActivityKind ?? AgentActivityKind.Status;
        Title = NormalizeHumanText(chunk.Text, 320);
        Detail = NormalizeHumanDetail(chunk.ActivityDetail);
        ActivityKey = chunk.ActivityKey;
        AssistantMessageId = chunk.AssistantMessageId;
        ElapsedMilliseconds = chunk.ElapsedMilliseconds;
        CreatedAt = DateTimeOffset.Now;
    }

    public AgentActivityKind Kind { get; }

    public string Title { get; }

    public string Detail { get; }

    public string DisplayText => string.IsNullOrWhiteSpace(Detail)
        ? Title
        : $"{Title} — {Detail}";

    public string? ActivityKey { get; }

    public string AssistantMessageId { get; }

    public DateTimeOffset CreatedAt { get; }

    public double? ElapsedMilliseconds { get; }

    public string Icon => Kind switch
    {
        AgentActivityKind.Planning => "\uE8C3",
        AgentActivityKind.ToolCall => "\uE90F",
        AgentActivityKind.ToolResult => "\uE73E",
        AgentActivityKind.Approval => "\uE72E",
        AgentActivityKind.Warning => "\uE7BA",
        AgentActivityKind.Error => "\uEA39",
        AgentActivityKind.Complete => "\uE930",
        _ => "\uE946"
    };

    public string Accent => Kind switch
    {
        AgentActivityKind.Approval => "#F7C873",
        AgentActivityKind.Warning => "#F7C873",
        AgentActivityKind.Error => "#F28B82",
        AgentActivityKind.Complete => "#8EE6B5",
        AgentActivityKind.ToolCall => "#8DDDF0",
        AgentActivityKind.ToolResult => "#A7E3B5",
        AgentActivityKind.Planning => "#C5B3FF",
        _ => "#B9D7EF"
    };

    public string TimingText => ElapsedMilliseconds is not { } elapsed
        ? CreatedAt.ToString("h:mm:ss tt")
        : elapsed < 1000
            ? $"{elapsed:0} ms"
            : $"{elapsed / 1000:0.00} s";

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    private static string NormalizeHumanDetail(string? value)
    {
        var normalized = NormalizeHumanText(value, 320);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(normalized);
            if (document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return "Technical payload omitted from the human activity view.";
            }
        }
        catch (JsonException)
        {
            // Ordinary human-readable detail is kept.
        }

        return normalized;
    }

    private static string NormalizeHumanText(string? value, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters] + "...";
    }
}
