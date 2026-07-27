using System.Text.RegularExpressions;

namespace AvatarBuilder.Modules.Audio.WakeWord;

internal sealed partial class EnglishWakePhraseTokenizer
{
	private static readonly IReadOnlyDictionary<string, string[]>
		PronunciationOverrides =
		new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
		{
			// CMU's ALI entry is AH-lee. The assistant name is commonly
			// pronounced AL-lee, represented by the ALLEY vowel sequence.
			["ALI"] = ["AE1 L IY0", "AA1 L IY0"]
		};

	private readonly Dictionary<string, string> _pronunciations =
		new(StringComparer.OrdinalIgnoreCase);

	internal EnglishWakePhraseTokenizer(string lexiconPath)
	{
		foreach (string line in File.ReadLines(lexiconPath))
		{
			int separator = line.IndexOf(' ');
			if (separator <= 0 || separator == line.Length - 1)
			{
				continue;
			}
			string word = line[..separator];
			int variant = word.IndexOf('(');
			if (variant > 0)
			{
				word = word[..variant];
			}
			_pronunciations.TryAdd(word, line[(separator + 1)..].Trim());
		}
	}

	internal bool TryBuild(
		string assistantName,
		out string encodedKeyword,
		out string status)
	{
		string[] words = WordPattern().Matches("HEY " + assistantName)
			.Select(match => match.Value.ToUpperInvariant())
			.ToArray();
		if (words.Length < 2)
		{
			encodedKeyword = "";
			status = "Assistant name does not contain a pronounceable word.";
			return false;
		}
		var alternatives = new List<string[]> (words.Length);
		foreach (string word in words)
		{
			if (PronunciationOverrides.TryGetValue(
				word,
				out string[]? overrides))
			{
				alternatives.Add(overrides);
				continue;
			}
			if (!_pronunciations.TryGetValue(word, out string? pronunciation))
			{
				encodedKeyword = "";
				status = $"The wake-word lexicon has no pronunciation for '{word}'.";
				return false;
			}
			alternatives.Add([pronunciation]);
		}
		string original = string.Join('_', words);
		IEnumerable<string> phrases = new[] { "" };
		foreach (string[] options in alternatives)
		{
			phrases = phrases.SelectMany(prefix => options.Select(option =>
				string.IsNullOrEmpty(prefix) ? option : prefix + " " + option));
		}
		encodedKeyword = string.Join('/', phrases.Select(phones =>
			phones + " :1.5 #0.25 @" + original));
		status = "Wake phrase encoded as " + original
			+ $" with {encodedKeyword.Count(value => value == '/') + 1} pronunciation(s)";
		return true;
	}

	[GeneratedRegex("[A-Za-z']+")]
	private static partial Regex WordPattern();
}
