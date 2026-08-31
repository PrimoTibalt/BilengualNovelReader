using System.Text;
using NovelReader.Domain.RealTimeReader.Vocabulary;

namespace NovelReader.Domain.RealTimeReader.Reading
{
	/// <summary>
	/// Turns a plain-text paragraph into the HTML the reading page renders, wrapping any
	/// term the reader has saved so it can be underlined and clicked.
	///
	/// Paragraph text is scraped from a third-party site and is therefore untrusted. Every
	/// character of it is HTML-escaped *before* any tag is added, so source text can never
	/// introduce an element or attribute. See D3 in DECISIONS.md.
	/// </summary>
	public static class ParagraphMarkupBuilder
	{
		/// <summary>Longest saved phrase that will be matched, in tokens (D4).</summary>
		public const int MaxPhraseTokens = 4;

		public const string KnownWordClass = "known-word";

		public static string BuildMarkup(string? paragraphText, IReadOnlySet<string> knownTerms)
		{
			if (string.IsNullOrEmpty(paragraphText))
			{
				return string.Empty;
			}

			if (knownTerms.Count == 0)
			{
				return HtmlEscape(paragraphText);
			}

			List<TokenSpan> tokens = Tokenize(paragraphText);
			if (tokens.Count == 0)
			{
				return HtmlEscape(paragraphText);
			}

			StringBuilder markup = new(paragraphText.Length + 64);
			int emittedUpTo = 0;
			int tokenIndex = 0;

			while (tokenIndex < tokens.Count)
			{
				int matchedTokenCount = FindLongestMatch(paragraphText, tokens, tokenIndex, knownTerms, out string normalizedTerm);
				if (matchedTokenCount == 0)
				{
					tokenIndex++;
					continue;
				}

				TokenSpan first = tokens[tokenIndex];
				TokenSpan last = tokens[tokenIndex + matchedTokenCount - 1];

				// Text between the previous match and this one, escaped verbatim.
				markup.Append(HtmlEscape(paragraphText[emittedUpTo..first.Start]));
				AppendKnownWord(markup, paragraphText[first.Start..last.End], normalizedTerm);

				emittedUpTo = last.End;
				tokenIndex += matchedTokenCount;
			}

			markup.Append(HtmlEscape(paragraphText[emittedUpTo..]));
			return markup.ToString();
		}

		private static void AppendKnownWord(StringBuilder markup, string surfaceForm, string normalizedTerm)
		{
			markup.Append("<span class=\"")
				.Append(KnownWordClass)
				.Append("\" data-term=\"")
				.Append(HtmlEscape(normalizedTerm))
				.Append("\">")
				.Append(HtmlEscape(surfaceForm))
				.Append("</span>");
		}

		/// <summary>
		/// Longest-match-first: a saved phrase "give up" beats a saved bare "give".
		/// Returns the number of tokens consumed, or 0 when nothing matches.
		/// </summary>
		private static int FindLongestMatch(
			string text,
			List<TokenSpan> tokens,
			int tokenIndex,
			IReadOnlySet<string> knownTerms,
			out string normalizedTerm)
		{
			int longestPossible = Math.Min(MaxPhraseTokens, tokens.Count - tokenIndex);
			for (int length = longestPossible; length >= 1; length--)
			{
				int start = tokens[tokenIndex].Start;
				int end = tokens[tokenIndex + length - 1].End;
				string candidate = TermNormalizer.Normalize(text[start..end]);
				if (candidate.Length > 0 && knownTerms.Contains(candidate))
				{
					normalizedTerm = candidate;
					return length;
				}
			}

			normalizedTerm = string.Empty;
			return 0;
		}

		/// <summary>
		/// Word spans in the paragraph. A token starts on a letter or digit and may contain
		/// an apostrophe or hyphen when a letter or digit follows it, so "don't" and
		/// "self-aware" stay single tokens.
		/// </summary>
		private static List<TokenSpan> Tokenize(string text)
		{
			List<TokenSpan> tokens = [];
			int index = 0;

			while (index < text.Length)
			{
				if (!TermNormalizer.IsTermCharacter(text[index]))
				{
					index++;
					continue;
				}

				int start = index;
				while (index < text.Length)
				{
					if (TermNormalizer.IsTermCharacter(text[index]))
					{
						index++;
						continue;
					}

					bool isInteriorJoiner = IsWordJoiner(text[index])
						&& index + 1 < text.Length
						&& TermNormalizer.IsTermCharacter(text[index + 1]);
					if (!isInteriorJoiner)
					{
						break;
					}

					index += 2;
				}

				tokens.Add(new TokenSpan(start, index));
			}

			return tokens;
		}

		private static bool IsWordJoiner(char character)
		{
			return character is '\'' or '’' or '-';
		}

		/// <summary>
		/// Escapes for both element text and double-quoted attribute values, which is why
		/// quotes are included.
		/// </summary>
		public static string HtmlEscape(string text)
		{
			if (text.Length == 0)
			{
				return string.Empty;
			}

			StringBuilder escaped = new(text.Length + 16);
			foreach (char character in text)
			{
				switch (character)
				{
					case '&': escaped.Append("&amp;"); break;
					case '<': escaped.Append("&lt;"); break;
					case '>': escaped.Append("&gt;"); break;
					case '"': escaped.Append("&quot;"); break;
					case '\'': escaped.Append("&#39;"); break;
					default: escaped.Append(character); break;
				}
			}

			return escaped.ToString();
		}

		private readonly record struct TokenSpan(int Start, int End);
	}
}
