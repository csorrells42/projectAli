using System.Text;
using Ali.Modules.Orchestration.State;

namespace Ali.Modules.Coordinator;

internal enum FinalAnswerPublicationDisposition
{
    Rejected = 0,
    DisplayedInMemory = 1,
    PersistedByConversationStore = 2
}

internal sealed record FinalAnswerPublication(
    string ConversationId,
    string UserMessageId,
    string AssistantMessageId,
    string PublicationId,
    string AnswerText,
    string AnswerDigest,
    Ali.Modules.Evidence.EvidenceStatus EvidenceStatus,
    string? FinishReason);

internal sealed record FinalAnswerPublicationAcknowledgment(
    string PublicationId,
    string AssistantMessageId,
    string AnswerDigest,
    FinalAnswerPublicationDisposition Disposition)
{
    internal static FinalAnswerPublicationAcknowledgment Displayed(
        FinalAnswerPublication publication) =>
        new(
            publication.PublicationId,
            publication.AssistantMessageId,
            publication.AnswerDigest,
            FinalAnswerPublicationDisposition.DisplayedInMemory);

    internal static FinalAnswerPublicationAcknowledgment Accepted(
        FinalAnswerPublication publication) =>
        new(
            publication.PublicationId,
            publication.AssistantMessageId,
            publication.AnswerDigest,
            FinalAnswerPublicationDisposition.PersistedByConversationStore);

    internal static FinalAnswerPublicationAcknowledgment Rejected(
        FinalAnswerPublication publication) =>
        new(
            publication.PublicationId,
            publication.AssistantMessageId,
            publication.AnswerDigest,
            FinalAnswerPublicationDisposition.Rejected);
}

internal static class FinalAnswerPublicationBoundary
{
    internal static FinalAnswerPublication BindExactPreparedAnswer(
        FinalAnswerPublication publication,
        string preparedAssistantMessageId,
        string preparedAnswerText,
        string preparedAnswerDigest)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (!string.Equals(
                preparedAssistantMessageId,
                publication.AssistantMessageId,
                StringComparison.Ordinal)
            || !string.Equals(preparedAnswerText, publication.AnswerText, StringComparison.Ordinal)
            || !string.Equals(
                preparedAnswerDigest,
                publication.AnswerDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                TurnStateIntegrity.Digest(publication.AnswerText),
                publication.AnswerDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Agent Framework output differs from the exact prepared final publication.");
        }

        return publication;
    }

    internal static void RequireExactAcknowledgment(
        FinalAnswerPublication publication,
        FinalAnswerPublicationAcknowledgment? acknowledgment)
    {
        RequireExactAcknowledgment(
            publication,
            acknowledgment,
            FinalAnswerPublicationDisposition.PersistedByConversationStore,
            "The conversation store did not persist the exact final publication.");
    }

    internal static void RequireExactInMemoryAcknowledgment(
        FinalAnswerPublication publication,
        FinalAnswerPublicationAcknowledgment? acknowledgment)
    {
        RequireExactAcknowledgment(
            publication,
            acknowledgment,
            FinalAnswerPublicationDisposition.DisplayedInMemory,
            "The live conversation sink did not display the exact final publication.");
    }

    private static void RequireExactAcknowledgment(
        FinalAnswerPublication publication,
        FinalAnswerPublicationAcknowledgment? acknowledgment,
        FinalAnswerPublicationDisposition requiredDisposition,
        string rejectionMessage)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (acknowledgment is null)
        {
            throw new InvalidOperationException(
                "The conversation stream returned no final-publication acknowledgment.");
        }

        if (acknowledgment.Disposition != requiredDisposition)
        {
            throw new InvalidOperationException(rejectionMessage);
        }

        if (!string.Equals(
                acknowledgment.PublicationId,
                publication.PublicationId,
                StringComparison.Ordinal)
            || !string.Equals(
                acknowledgment.AssistantMessageId,
                publication.AssistantMessageId,
                StringComparison.Ordinal)
            || !string.Equals(
                acknowledgment.AnswerDigest,
                publication.AnswerDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The conversation stream acknowledged a different final publication.");
        }
    }
}

/// <summary>
/// One-shot handoff from the producer to the desktop conversation sink. Channel admission is
/// intentionally not an acknowledgment: the sink completes this delivery only after the exact
/// assistant message has reached the required live-display or durable-persistence boundary.
/// </summary>
internal sealed class FinalAnswerPublicationDelivery
{
    private readonly TaskCompletionSource<FinalAnswerPublicationAcknowledgment> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FinalAnswerPublicationDisposition _requiredDisposition;

    internal FinalAnswerPublicationDelivery(
        FinalAnswerPublication publication,
        FinalAnswerPublicationDisposition requiredDisposition =
            FinalAnswerPublicationDisposition.PersistedByConversationStore)
    {
        Publication = publication ?? throw new ArgumentNullException(nameof(publication));
        if (requiredDisposition is not FinalAnswerPublicationDisposition.DisplayedInMemory
            and not FinalAnswerPublicationDisposition.PersistedByConversationStore)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredDisposition));
        }

        _requiredDisposition = requiredDisposition;
    }

    internal FinalAnswerPublication Publication { get; }

    internal bool RequiresPersistence =>
        _requiredDisposition == FinalAnswerPublicationDisposition.PersistedByConversationStore;

    internal string GetUnpublishedSuffix(string streamedText)
    {
        ArgumentNullException.ThrowIfNull(streamedText);
        if (!Publication.AnswerText.StartsWith(streamedText, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The streamed assistant text does not match the final publication prefix.");
        }

        return Publication.AnswerText[streamedText.Length..];
    }

    internal async ValueTask<FinalAnswerPublicationAcknowledgment> WaitAsync(
        CancellationToken cancellationToken) =>
        await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

    internal void AcknowledgeDisplayed(
        string conversationId,
        string assistantMessageId,
        string exactAnswerText) =>
        Acknowledge(
            conversationId,
            assistantMessageId,
            exactAnswerText,
            FinalAnswerPublicationDisposition.DisplayedInMemory);

    internal void AcknowledgePersisted(
        string conversationId,
        string assistantMessageId,
        string exactAnswerText) =>
        Acknowledge(
            conversationId,
            assistantMessageId,
            exactAnswerText,
            FinalAnswerPublicationDisposition.PersistedByConversationStore);

    private void Acknowledge(
        string conversationId,
        string assistantMessageId,
        string exactAnswerText,
        FinalAnswerPublicationDisposition disposition)
    {
        if (disposition != _requiredDisposition)
        {
            _completion.TrySetException(new InvalidOperationException(
                "The conversation sink used the wrong final-publication acknowledgment boundary."));
            return;
        }

        if (!string.Equals(conversationId, Publication.ConversationId, StringComparison.Ordinal)
            || !string.Equals(
                assistantMessageId,
                Publication.AssistantMessageId,
                StringComparison.Ordinal)
            || !string.Equals(exactAnswerText, Publication.AnswerText, StringComparison.Ordinal)
            || !string.Equals(
                TurnStateIntegrity.Digest(exactAnswerText),
                Publication.AnswerDigest,
                StringComparison.Ordinal))
        {
            _completion.TrySetException(new InvalidDataException(
                "The conversation sink attempted to acknowledge a different final publication."));
            return;
        }

        _completion.TrySetResult(
            disposition == FinalAnswerPublicationDisposition.DisplayedInMemory
                ? FinalAnswerPublicationAcknowledgment.Displayed(Publication)
                : FinalAnswerPublicationAcknowledgment.Accepted(Publication));
    }

    internal void Reject(string reason)
    {
        var bounded = string.IsNullOrWhiteSpace(reason)
            ? "The conversation sink rejected the final publication."
            : reason.Trim();
        _completion.TrySetException(new InvalidOperationException(bounded));
    }
}

internal static class FinalAnswerRenderer
{
    internal const int MaximumVisibleSources = 5;

    internal static string Compose(
        string answerText,
        IReadOnlyList<CoordinatorSourceItem> sources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(answerText);
        ArgumentNullException.ThrowIfNull(sources);

        var usableSources = sources
            .Where(source => Uri.TryCreate(source.Url, UriKind.Absolute, out var uri)
                && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(source => source.Url, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumVisibleSources)
            .ToArray();
        if (usableSources.Length == 0)
        {
            return answerText;
        }

        var rendered = new StringBuilder(answerText)
            .AppendLine()
            .AppendLine()
            .AppendLine("Sources checked:");
        foreach (var source in usableSources)
        {
            var safeName = source.Name.Replace('[', '(').Replace(']', ')').Trim();
            rendered.Append("- [")
                .Append(string.IsNullOrWhiteSpace(safeName) ? source.Url : safeName)
                .Append("](")
                .Append(source.Url)
                .AppendLine(")");
        }

        return rendered.ToString().TrimEnd();
    }
}
