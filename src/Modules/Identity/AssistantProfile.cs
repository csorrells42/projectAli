namespace Ali.Modules.Identity;

public sealed record AssistantProfile(
    string AssistantName,
    string ProfileId,
    DateTimeOffset CreatedAt)
{
    public const string DefaultAssistantName = "Ali";

    public static AssistantProfile CreateDefault() =>
        new(DefaultAssistantName, CreateProfileId(DefaultAssistantName), DateTimeOffset.UtcNow);

    public static AssistantProfile Create(string? assistantName) =>
        new(NormalizeAssistantName(assistantName), CreateProfileId(assistantName), DateTimeOffset.UtcNow);

    public AssistantProfile Normalize() =>
        this with
        {
            AssistantName = NormalizeAssistantName(AssistantName),
            ProfileId = NormalizeProfileId(ProfileId, AssistantName),
            CreatedAt = CreatedAt == default ? DateTimeOffset.UtcNow : CreatedAt
        };

    public static string NormalizeAssistantName(string? assistantName)
    {
        var normalized = string.Join(' ', (assistantName ?? string.Empty)
            .Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? DefaultAssistantName : normalized;
    }

    private static string CreateProfileId(string? assistantName)
    {
        var normalized = NormalizeAssistantName(assistantName);
        var safeName = new string(normalized
            .Where(char.IsLetterOrDigit)
            .Take(24)
            .ToArray());
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = DefaultAssistantName;
        }

        var suffix = Guid.NewGuid().ToString("N")[..12];
        return $"{safeName}-{suffix}";
    }

    private static string NormalizeProfileId(string? profileId, string assistantName)
    {
        var normalized = string.IsNullOrWhiteSpace(profileId)
            ? CreateProfileId(assistantName)
            : Path.GetFileName(profileId.Trim());
        return string.IsNullOrWhiteSpace(normalized) ? CreateProfileId(assistantName) : normalized;
    }
}
