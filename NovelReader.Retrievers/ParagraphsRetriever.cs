using System.Net;
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

		/// <summary>
		/// The title of the page the site answers, with 200, for a chapter it does not have. Its
		/// body is the "page moved" notice below, which D21 took for a notice about moved pages.
		/// </summary>
		private const string NotFoundTitle = "Chapter Not Found";

		/// <summary>
		/// What the site served. A chapter it does not have is a <see cref="ChapterNotFoundException"/>,
		/// a page that is merely not the chapter this time a <see cref="ChapterUnavailableException"/> (D49).
		/// </summary>
		public async Task<Dictionary<int, string>> GetParagraphsAsync(string uriPath)
		{
			HttpClient httpClient = httpClientFactory.CreateClient(ServiceCollectionExtension.NovelSourceClient);
			using HttpResponseMessage response = await httpClient.GetAsync(uriPath);

			// novelfire.net sent about one request in six to another site's "Loading..." page, and
			// in the end every one (see the base address). The client does not follow redirects,
			// so this is seen for what it is rather than as a page with no paragraphs.
			if ((int)response.StatusCode is >= 300 and < 400)
			{
				throw new ChapterUnavailableException(
					$"'{uriPath}' was redirected to {response.Headers.Location?.Host ?? "elsewhere"}.");
			}

			if (response.StatusCode == HttpStatusCode.NotFound)
			{
				throw new ChapterNotFoundException($"'{uriPath}' does not exist.");
			}

			if (!response.IsSuccessStatusCode)
			{
				throw new ChapterUnavailableException($"'{uriPath}' answered {(int)response.StatusCode}.");
			}

			HtmlDocument doc = new();
			doc.Load(await response.Content.ReadAsStreamAsync());
			string? title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText;
			if (title is not null && title.Contains(NotFoundTitle, StringComparison.OrdinalIgnoreCase))
			{
				throw new ChapterNotFoundException($"'{uriPath}' is the site's \"chapter not found\" page.");
			}

			HtmlNodeCollection? paragraphNodes = doc.DocumentNode.SelectNodes("//div[@id='content']/p");
			if (paragraphNodes is null)
			{
				throw new ChapterUnavailableException($"No paragraphs found at '{uriPath}'. The page markup may have changed.");
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
				throw new ChapterNotFoundException(
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
