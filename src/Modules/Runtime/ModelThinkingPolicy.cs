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
    public static string Describe(
        ModelThinkingControl control,
        bool thinkingEnabled,
        string reasoningEffort) =>
        control switch
        {
            ModelThinkingControl.GptOssReasoningEffort =>
                $"chat_template_kwargs.reasoning_effort={reasoningEffort}",
            ModelThinkingControl.QwenTemplateToggle =>
                $"chat_template_kwargs.enable_thinking={thinkingEnabled.ToString().ToLowerInvariant()}",
            ModelThinkingControl.GemmaSystemPromptToken =>
                thinkingEnabled ? "Gemma system thinking token enabled" : "Gemma system thinking token omitted",
            _ => "no thinking control required; no thinking field sent"
        };
}
