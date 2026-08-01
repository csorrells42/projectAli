using System.Security.Cryptography;
using System.Text;

namespace Ali.Modules.Orchestration.Contracts;

public sealed class TurnIdentity : IEquatable<TurnIdentity>
{
    public TurnIdentity(string userId, string conversationId, string assistantMessageId)
    {
        UserId = RequireValue(userId, nameof(userId));
        ConversationId = RequireValue(conversationId, nameof(conversationId));
        AssistantMessageId = RequireValue(assistantMessageId, nameof(assistantMessageId));
    }

    public string UserId { get; }

    public string ConversationId { get; }

    public string AssistantMessageId { get; }

    public bool Equals(TurnIdentity? other) =>
        other is not null &&
        string.Equals(UserId, other.UserId, StringComparison.Ordinal) &&
        string.Equals(ConversationId, other.ConversationId, StringComparison.Ordinal) &&
        string.Equals(AssistantMessageId, other.AssistantMessageId, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as TurnIdentity);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(UserId),
        StringComparer.Ordinal.GetHashCode(ConversationId),
        StringComparer.Ordinal.GetHashCode(AssistantMessageId));

    public static bool operator ==(TurnIdentity? left, TurnIdentity? right) => Equals(left, right);

    public static bool operator !=(TurnIdentity? left, TurnIdentity? right) => !Equals(left, right);

    internal string StorageKey
    {
        get
        {
            var material = $"{UserId.Length}:{UserId}|{ConversationId.Length}:{ConversationId}|{AssistantMessageId.Length}:{AssistantMessageId}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        }
    }

    private static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A turn identity component cannot be empty.", parameterName);
        }

        return value;
    }
}
