using System.Globalization;
using System.Text;

namespace NovelReader.Domain.RealTimeReader.Vocabulary
{
	/// <summary>
	/// Reduces a surface form found in a novel ("Ephemeral," / "ephemeral's") to the key a
	/// vocabulary entry is stored under. See D4 in DECISIONS.md.
	/// </summary>
	public static class TermNormalizer
	{
		private static readonly char[] PossessiveApostrophes = ['\'', '’'];

		/// <summary>
		/// Normalises a single word or a whitespace-separated phrase. Returns an empty string
		/// when nothing meaningful remains, which callers treat as "not a term".
		/// </summary>
		public static string Normalize(string? surfaceForm)
		{
			if (string.IsNullOrWhiteSpace(surfaceForm))
			{
				return string.Empty;
			}

			string[] words = surfaceForm.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			StringBuilder normalized = new(surfaceForm.Length);
			foreach (string word in words)
			{
				string normalizedWord = NormalizeWord(word);
				if (normalizedWord.Length == 0)
				{
					continue;
				}

				if (normalized.Length > 0)
				{
					normalized.Append(' ');
				}

				normalized.Append(normalizedWord);
			}

			return normalized.ToString();
		}

		private static string NormalizeWord(string word)
		{
			string trimmed = word.Normalize(NormalizationForm.FormC)
				.ToLower(CultureInfo.InvariantCulture);

			int start = 0;
			int end = trimmed.Length - 1;
			while (start <= end && !IsTermCharacter(trimmed[start]))
			{
				start++;
			}

			while (end >= start && !IsTermCharacter(trimmed[end]))
			{
				end--;
			}

			if (start > end)
			{
				return string.Empty;
			}

			// Trimming only touches the ends, so interior hyphens and apostrophes survive
			// and "self-aware" / "don't" stay intact.
			string core = trimmed[start..(end + 1)];
			return StripPossessive(core);
		}

		private static string StripPossessive(string word)
		{
			if (word.Length <= 2 || word[^1] != 's')
			{
				return word;
			}

			return Array.IndexOf(PossessiveApostrophes, word[^2]) >= 0 ? word[..^2] : word;
		}

		/// <summary>
		/// Characters that can begin or end a term. Punctuation outside this set is trimmed
		/// from the edges of a surface form.
		/// </summary>
		public static bool IsTermCharacter(char character)
		{
			return char.IsLetterOrDigit(character);
		}
	}
}
