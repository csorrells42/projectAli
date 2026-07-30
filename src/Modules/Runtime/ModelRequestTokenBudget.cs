namespace Ali.Modules.Runtime;

internal sealed record ModelRequestTokenBudget(
    int ContextTokens,
    int RequestedOutputTokens,
    int EstimatedInputTokens,
    int SafetyReserveTokens,
    int EffectiveOutputTokens)
{
    public bool WasClamped => EffectiveOutputTokens < RequestedOutputTokens;
}

internal static class ModelRequestTokenBudgetCalculator
{
    private const int MinimumUsefulOutputTokens = 128;
    private const int CharactersPerEstimatedToken = 3;
    private const int TokensPerImage = 1024;
    private const int TokensPerMessage = 12;
    private const int TokensPerTool = 24;

    public static ModelRequestTokenBudget Calculate(
        int contextTokens,
        int requestedOutputTokens,
        IEnumerable<string?> textSegments,
        IEnumerable<string?> toolSchemas,
        int messageCount,
        int toolCount,
        int imageCount)
    {
        if (contextTokens < 512)
        {
            throw new ArgumentOutOfRangeException(nameof(contextTokens));
        }

        if (requestedOutputTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedOutputTokens));
        }

        var textCharacters = CountCharacters(textSegments);
        var schemaCharacters = CountCharacters(toolSchemas);
        var estimatedInputTokens = DivideRoundUp(
                checked(textCharacters + schemaCharacters),
                CharactersPerEstimatedToken)
            + checked(Math.Max(0, messageCount) * TokensPerMessage)
            + checked(Math.Max(0, toolCount) * TokensPerTool)
            + checked(Math.Max(0, imageCount) * TokensPerImage);
        var safetyReserveTokens = Math.Max(256, DivideRoundUp(contextTokens, 20));
        var availableOutputTokens = contextTokens - estimatedInputTokens - safetyReserveTokens;

        if (availableOutputTokens < MinimumUsefulOutputTokens)
        {
            throw new ModelContextCapacityException(
                contextTokens,
                estimatedInputTokens,
                safetyReserveTokens,
                MinimumUsefulOutputTokens);
        }

        return new ModelRequestTokenBudget(
            contextTokens,
            requestedOutputTokens,
            estimatedInputTokens,
            safetyReserveTokens,
            Math.Min(requestedOutputTokens, availableOutputTokens));
    }

    private static int CountCharacters(IEnumerable<string?> values)
    {
        long total = 0;
        foreach (var value in values)
        {
            total += value?.Length ?? 0;
            if (total > int.MaxValue)
            {
                throw new ModelContextCapacityException(
                    int.MaxValue,
                    int.MaxValue,
                    0,
                    MinimumUsefulOutputTokens);
            }
        }

        return (int)total;
    }

    private static int DivideRoundUp(int value, int divisor) =>
        checked((value + divisor - 1) / divisor);
}

internal sealed class ModelContextCapacityException : InvalidOperationException
{
    public ModelContextCapacityException(
        int contextTokens,
        int estimatedInputTokens,
        int safetyReserveTokens,
        int minimumOutputTokens)
        : base(
            $"This turn is too large for the selected {contextTokens:N0}-token context. "
            + $"Ali estimates {estimatedInputTokens:N0} input tokens and reserves {safetyReserveTokens:N0} safety tokens, "
            + $"leaving fewer than {minimumOutputTokens:N0} tokens for a useful answer. "
            + "No request was sent to the model. Select a larger context, start a new conversation, or reduce the attached material.")
    {
    }
}
