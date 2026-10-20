using System.Text;
using NovelReader.Domain.RealTimeReader.Reading;

namespace NovelReader.Dictionary
{
	/// <summary>
	/// Asks Jev whether a scraped page is a chapter (D47). Measured on hand-made pages: site
	/// pages (moved notice, browser check, paywall, 404, "Loading...") scored 0.04 or less, real
	/// chapters — an author's announcement and a four-line chapter among them — 0.83 or more.
	/// </summary>
	internal sealed class JevChapterTextCheck(JevClient jev) : IChapterTextCheck
	{
		/// <summary>
		/// A page is refused only when Jev is this sure it is not a chapter. Far below where any
		/// chapter scored: refusing a real one would keep the reader from it.
		/// </summary>
		internal const double MaximumChapterProbability = 0.2;

		/// <summary>The opening says what a page is; a long chapter's tail would only add cost.</summary>
		private const int MaximumParagraphs = 20;

		private const int MaximumLength = 4000;

		private const string Instructions = """
			These are the paragraphs scraped from a web page that should hold one chapter of a web
			novel. Is this a chapter of the novel? Anything the author posted as a chapter counts,
			including an author's note or announcement. A page from the website itself does not: a
			notice that the page moved, an error or not-found page, a login or paywall prompt, a
			captcha or browser check, or a placeholder.
			""";

		public async Task<bool> IsSitePageAsync(
			IReadOnlyDictionary<int, string> paragraphs,
			CancellationToken cancellationToken = default)
		{
			double? chapter = await jev.YesProbabilityAsync(
				Opening(paragraphs),
				Instructions,
				"a chapter of the novel, or the author's own note posted as one",
				"a page from the website: notice, error, login, paywall, captcha, placeholder",
				cancellationToken);

			return chapter < MaximumChapterProbability;
		}

		/// <summary>The first paragraphs in order, one per line, cut at <see cref="MaximumLength"/>.</summary>
		internal static string Opening(IReadOnlyDictionary<int, string> paragraphs)
		{
			StringBuilder text = new();
			foreach (int number in paragraphs.Keys.Order().Take(MaximumParagraphs))
			{
				text.AppendLine(paragraphs[number].Trim());
				if (text.Length >= MaximumLength)
				{
					break;
				}
			}

			return text.Length > MaximumLength ? text.ToString(0, MaximumLength) : text.ToString().TrimEnd();
		}
	}
}
