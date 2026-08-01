namespace Ali.Modules.Runtime;

public enum ModelThinkingControl
{
    None,
    GptOssReasoningEffort,
    QwenTemplateToggle,
    GemmaSystemPromptToken
}

public static class ModelThinkingPolicy
{
    public static ModelThinkingControl Resolve(string? model, string? family)
    {
        if (OllamaRuntimeSafetyPolicy.IsGptOssModel(model)
            || OllamaRuntimeSafetyPolicy.IsGptOssModel(family))
        {
            return ModelThinkingControl.GptOssReasoningEffort;
        }

        if (string.Equals(family?.Trim(), "Gemma", StringComparison.OrdinalIgnoreCase)
            || model?.Trim().StartsWith("gemma-4-", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ModelThinkingControl.GemmaSystemPromptToken;
        }

        if (IsQwenHybridThinkingModel(model, family))
        {
            return ModelThinkingControl.QwenTemplateToggle;
        }

        return ModelThinkingControl.None;
    }

    public static string Describe(string? model, string? family, bool thinkingEnabled, string reasoningEffort) =>
        Resolve(model, family) switch
        {
            ModelThinkingControl.GptOssReasoningEffort =>
                $"chat_template_kwargs.reasoning_effort={reasoningEffort}",
            ModelThinkingControl.QwenTemplateToggle =>
                $"chat_template_kwargs.enable_thinking={thinkingEnabled.ToString().ToLowerInvariant()}",
            ModelThinkingControl.GemmaSystemPromptToken =>
                thinkingEnabled ? "Gemma system thinking token enabled" : "Gemma system thinking token omitted",
            _ => "no thinking control required; no thinking field sent"
        };

    private static bool IsQwenHybridThinkingModel(string? model, string? family)
    {
        var normalizedModel = model?.Trim() ?? string.Empty;
        if (normalizedModel.Contains("coder", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalizedModel.StartsWith("qwq", StringComparison.OrdinalIgnoreCase)
               || normalizedModel.StartsWith("qwen3-", StringComparison.OrdinalIgnoreCase)
               || string.Equals(family?.Trim(), "Qwen Thinking", StringComparison.OrdinalIgnoreCase);
    }
}
