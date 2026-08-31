using System.Net;
using System.Text;

namespace NovelReader.Dictionary
{
	/// <summary>
	/// Wiktionary returns definitions as HTML fragments (wiki links, sense labels). The
	/// reading page wants plain text, so tags are removed and entities decoded (D1).
	/// </summary>
	internal static class HtmlFragmentStripper
	{
		public static string ToPlainText(string? htmlFragment)
		{
			if (string.IsNullOrWhiteSpace(htmlFragment))
			{
				return string.Empty;
			}

			StringBuilder withoutTags = new(htmlFragment.Length);
			bool insideTag = false;
			foreach (char character in htmlFragment)
			{
				if (character == '<')
				{
					insideTag = true;
					continue;
				}

				if (character == '>')
				{
					insideTag = false;
					continue;
				}

				if (!insideTag)
				{
					withoutTags.Append(character);
				}
			}

			// Tags are removed before entities are decoded, so a decoded '<' can never be
			// mistaken for markup. The result is plain text and callers must render it as
			// text, never as HTML.
			string decoded = WebUtility.HtmlDecode(withoutTags.ToString());
			return CollapseWhitespace(decoded);
		}

		private static string CollapseWhitespace(string text)
		{
			StringBuilder collapsed = new(text.Length);
			bool previousWasSpace = false;
			foreach (char character in text)
			{
				bool isSpace = char.IsWhiteSpace(character) || character == ' ';
				if (isSpace)
				{
					if (collapsed.Length > 0 && !previousWasSpace)
					{
						collapsed.Append(' ');
					}

					previousWasSpace = true;
					continue;
				}

				collapsed.Append(character);
				previousWasSpace = false;
			}

			return collapsed.ToString().TrimEnd();
		}
	}
}
