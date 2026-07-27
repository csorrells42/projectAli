using System;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;

namespace AvatarBuilder.Modules.Audio.SpeechToText;

/// <summary>
/// Common drop-in contract for authorized utterance transcription modules.
/// Implementations publish the same immutable output and own their workers.
/// </summary>
public interface ISpeechToTextModule :
	IAudioModule,
	IModuleOutputSource<TranscriptionOutput>,
	IDisposable
{
	string ProviderName { get; }

	bool IsConfigured { get; }
}
