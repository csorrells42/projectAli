namespace AvatarBuilder.Modules.Audio.WakeWord;

public static class WakeWordModuleSelfTest
{
	public static string Run(string modelFolder)
	{
		WakeWordModelInfo model = WakeWordModelInfo.Load(modelFolder);
		if (!model.IsReady)
		{
			throw new InvalidOperationException(model.Status);
		}
		var tokenizer = new EnglishWakePhraseTokenizer(
			model.EnglishLexiconPath);
		if (!tokenizer.TryBuild("Ali", out string phrase, out string status))
		{
			throw new InvalidOperationException(status);
		}
		const string expected =
			"HH EY1 AE1 L IY0 :1.5 #0.25 @HEY_ALI/"
			+ "HH EY1 AA1 L IY0 :1.5 #0.25 @HEY_ALI";
		if (!string.Equals(phrase, expected, StringComparison.Ordinal))
		{
			throw new InvalidOperationException(
				"Wake phrase tokenization was incorrect: " + phrase);
		}
		using var backend = new SherpaWakeWordBackend("Ali", model);
		WakeWordEvidence silence = backend.Detect(
			new float[16000],
			16000);
		if (silence.Detected)
		{
			throw new InvalidOperationException(
				"Silence incorrectly triggered the wake phrase.");
		}
		return "PASS: packaged Sherpa KWS model initialized, Hey Ali included AL-lee and AH-lee phoneme paths, tail padding completed decoding, and silence did not trigger.";
	}
}
