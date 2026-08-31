using HtmlAgilityPack;
using NovelReader.Domain.RealTimeReader.Parsing;

namespace NovelReader.Retrievers
{
	public class ParagraphsRetriever(IHttpClientFactory httpClientFactory) : IParagraphsRetriever
	{
		/// <summary>
		/// Phrases from the site's "this page moved, use search" notice. It answers 200 and
		/// puts the notice inside the same <c>#content</c> div a chapter uses, so without this
		/// it scrapes cleanly as a short chapter — and then gets cached as one (D21).
		/// </summary>
		private static readonly string[] PlaceholderMarkers =
		[
			"moved for better user experience",
			"use the search function"
		];

		/// <summary>
		/// A real chapter is never this short. Used together with the markers so that a
		/// genuinely brief chapter is not mistaken for the notice.
		/// </summary>
		private const int MaximumPlaceholderParagraphs = 3;

		public async Task<Dictionary<int, string>> GetParagraphsAsync(string uriPath)
		{
			HttpClient httpClient = httpClientFactory.CreateClient("NovelFire");
			HttpResponseMessage response = await httpClient.GetAsync(uriPath);
			response.EnsureSuccessStatusCode();

			HtmlDocument doc = new();
			doc.Load(await response.Content.ReadAsStreamAsync());
			HtmlNodeCollection? paragraphNodes = doc.DocumentNode.SelectNodes("//div[@id='content']/p");
			if (paragraphNodes is null)
			{
				throw new InvalidOperationException($"No paragraphs found at '{uriPath}'. The page markup may have changed.");
			}

			Dictionary<int, string> result = new(paragraphNodes.Count);
			for (var i = 1; i <= paragraphNodes.Count; i++)
			{
				HtmlNode paragraphNode = paragraphNodes[i-1];
				result[i] = paragraphNode.InnerText;
			}

			if (IsMovedPagePlaceholder(result))
			{
				// Thrown rather than returned so nothing caches it: the caller only stores a
				// chapter once the scrape succeeds.
				throw new InvalidOperationException(
					$"'{uriPath}' is the site's \"page moved\" notice, not a chapter.");
			}

			return result;
		}

		private static bool IsMovedPagePlaceholder(Dictionary<int, string> paragraphs)
		{
			if (paragraphs.Count is 0 or > MaximumPlaceholderParagraphs)
			{
				return false;
			}

			string joined = string.Join(' ', paragraphs.Values);
			foreach (string marker in PlaceholderMarkers)
			{
				if (joined.Contains(marker, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}

			return false;
		}
	}
}
