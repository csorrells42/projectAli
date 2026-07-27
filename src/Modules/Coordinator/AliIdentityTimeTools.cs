using Ali.Modules.Identity;
using Ali.Modules.Time;

namespace Ali.Modules.Coordinator;

public sealed class AliIdentityTimeTools(AssistantProfile assistantProfile)
{
    private readonly AssistantProfile _assistantProfile = assistantProfile.Normalize();

    public CoordinatorIdentityResult GetAssistantIdentity() =>
        new(
            _assistantProfile.AssistantName,
            _assistantProfile.ProfileId,
            "This is the configured local assistant identity. It is separate from the human user's identity and from the underlying model package.");

    public string GetCurrentLocalTime() =>
        CurrentDateTimeSnapshot.Capture().BuildCompactFactLine();
}
