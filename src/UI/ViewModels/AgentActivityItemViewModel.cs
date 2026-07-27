using Ali.Modules.Coordinator;

namespace Ali.UI.ViewModels;

public sealed class AgentActivityItemViewModel
{
    public AgentActivityItemViewModel(AssistantStreamChunk chunk)
    {
        Kind = chunk.ActivityKind ?? AgentActivityKind.Status;
        Title = chunk.Text;
        Detail = chunk.ActivityDetail ?? string.Empty;
        ElapsedMilliseconds = chunk.ElapsedMilliseconds;
        CreatedAt = DateTimeOffset.Now;
    }

    public AgentActivityKind Kind { get; }

    public string Title { get; }

    public string Detail { get; }

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
}
