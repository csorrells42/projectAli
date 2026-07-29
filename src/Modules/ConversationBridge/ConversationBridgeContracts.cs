namespace Ali.Modules.ConversationBridge;

public sealed record ConversationBridgeTurnRequest(string Text);

public sealed record ConversationBridgeRenderBlock(
    string Kind,
    string Text = "",
    int? Level = null,
    string? Marker = null,
    IReadOnlyList<string>? Headers = null,
    IReadOnlyList<IReadOnlyList<string>>? Rows = null);

public sealed record ConversationBridgeMessage(
    string Id,
    string Role,
    string Text,
    string EvidenceStatus,
    DateTimeOffset CreatedAt,
    bool IsFlaggedForCorrection,
    IReadOnlyList<ConversationBridgeRenderBlock> RenderBlocks);

public sealed record ConversationBridgeActivity(
    string Kind,
    string Title,
    string Detail,
    DateTimeOffset CreatedAt,
    double? ElapsedMilliseconds);

public sealed record ConversationBridgeApprovalWait(
    string RequestId,
    string ToolName,
    string Arguments,
    string Description);

public sealed record ConversationBridgeSnapshot(
    string AssistantName,
    string ConversationId,
    bool IsBusy,
    string Status,
    string ActivitySummary,
    ConversationBridgeApprovalWait? WaitingForUserApproval,
    IReadOnlyList<ConversationBridgeMessage> Messages,
    IReadOnlyList<ConversationBridgeActivity> Activities,
    DateTimeOffset CapturedAt);

public sealed record ConversationBridgeRuntimeStatus(
    bool IsRunning,
    string State,
    string Message,
    string Endpoint);
