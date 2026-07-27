using AvatarBuilder.Modules.Audio.SpeakerRecognition;
using AvatarBuilder.Modules.Audio.WakeWord;

namespace AvatarBuilder.Modules.Security;

public static class AliSecurityPolicy
{
	public static SecurityDecision Evaluate(
		PushToTalkEvidence pushToTalk,
		LoginEvidence login,
		TargetAuthorizationEvidence target,
		bool hasVisualAttention,
		SpeakerRecognitionEvidence speaker,
		WakeWordEvidence wake)
	{
		if (speaker.IsEnrollmentUtterance)
		{
			return Deny(
				"Voice enrollment utterances never enter normal speech-to-text.");
		}

		AttentionGrantSource source = AttentionGrantSource.None;
		if (hasVisualAttention)
		{
			source |= AttentionGrantSource.Visual;
		}
		if (wake.Detected)
		{
			source |= AttentionGrantSource.WakeWord;
		}
		if (pushToTalk.IsEnabled && pushToTalk.IsPressed)
		{
			source |= AttentionGrantSource.PushToTalk;
		}
		if (source == AttentionGrantSource.None)
		{
			return Deny(
				"No visual, wake-word, or push-to-talk attention was detected.");
		}

		string visualId = target.PersonIdentityId?.Trim() ?? "";
		string voiceId = speaker.IsKnown
			? speaker.PersonIdentityId?.Trim() ?? ""
			: "";
		double visualConfidence = Math.Clamp(
			target.VisualIdentityConfidence,
			0d,
			1d);
		double voiceConfidence = Math.Clamp(
			speaker.Similarity,
			0d,
			1d);
		bool signalsAgree = !string.IsNullOrWhiteSpace(visualId)
			&& !string.IsNullOrWhiteSpace(voiceId)
			&& string.Equals(
				visualId,
				voiceId,
				StringComparison.OrdinalIgnoreCase);
		(string id, string name, bool known) participant =
			ChooseParticipant(login, target, speaker);

		return new SecurityDecision(
			true,
			"Attention",
			participant.id,
			participant.name,
			participant.known,
			"Unified attention gate opened by " + FormatSources(source) + ".",
			source,
			visualId,
			voiceId,
			visualConfidence,
			voiceConfidence,
			signalsAgree);
	}

	private static (string Id, string Name, bool Known) ChooseParticipant(
		LoginEvidence login,
		TargetAuthorizationEvidence target,
		SpeakerRecognitionEvidence speaker)
	{
		if (target.IsAuthorized
			&& !string.IsNullOrWhiteSpace(target.PersonIdentityId))
		{
			return (
				target.PersonIdentityId,
				string.IsNullOrWhiteSpace(target.DisplayName)
					? "Unknown"
					: target.DisplayName,
				true);
		}
		if (speaker.IsKnown
			&& !string.IsNullOrWhiteSpace(speaker.PersonIdentityId))
		{
			return (
				speaker.PersonIdentityId,
				"Known speaker",
				true);
		}
		if (login.IsAvailable && login.IsAuthenticated)
		{
			return (
				login.PersonIdentityId,
				string.IsNullOrWhiteSpace(login.DisplayName)
					? "Authenticated user"
					: login.DisplayName,
				true);
		}
		return ("", "Unknown", false);
	}

	private static string FormatSources(AttentionGrantSource source)
	{
		var values = new List<string>(3);
		if (source.HasFlag(AttentionGrantSource.Visual))
		{
			values.Add("visual attention");
		}
		if (source.HasFlag(AttentionGrantSource.WakeWord))
		{
			values.Add("wake word");
		}
		if (source.HasFlag(AttentionGrantSource.PushToTalk))
		{
			values.Add("push to talk");
		}
		return string.Join(", ", values);
	}

	private static SecurityDecision Deny(string reason) =>
		new(false, "Closed", "", "Unknown", false, reason);
}
