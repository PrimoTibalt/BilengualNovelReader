using System.Net;
using NovelReader.Domain.RealTimeReader.Parsing;
using NovelReader.Retrievers;

namespace NovelReader.Tests
{
	/// <summary>
	/// Scraping a chapter page, and telling one apart from the site's "page moved" notice —
	/// which answers 200 and sits in the same container a chapter does (D21) — and a chapter the
	/// site does not have from one it merely did not serve this time (D49).
	/// </summary>
	public class ParagraphsRetrieverTests
	{
		private static ParagraphsRetriever Build(string html, HttpStatusCode status = HttpStatusCode.OK)
		{
			return new ParagraphsRetriever(
				new StubHttpClientFactory(new StubHttpMessageHandler(status, html), "https://example.test/"));
		}

		private static string Page(params string[] paragraphs)
		{
			string body = string.Concat(paragraphs.Select(text => $"<p>{text}</p>"));
			return $"<html><body><div id='content'>{body}</div></body></html>";
		}

		[Fact]
		public async Task A_chapter_page_scrapes_into_numbered_paragraphs()
		{
			ParagraphsRetriever retriever = Build(Page("Chapter One", "First.", "Second.", "Third."));

			Dictionary<int, string> paragraphs = await retriever.GetParagraphsAsync("novel/x/chapter-1");

			Assert.Equal(4, paragraphs.Count);
			Assert.Equal("Chapter One", paragraphs[1]);
			Assert.Equal("Third.", paragraphs[4]);
		}

		[Fact]
		public async Task The_moved_page_notice_is_not_a_chapter()
		{
			// The real notice for a chapter number that does not exist.
			ParagraphsRetriever retriever = Build(Page(
				"Some novel pages moved for better user experience. Could be affected by this situation.",
				"Please use the search function for the content you want to access or go to home page and start exploring the novels."));

			ChapterNotFoundException thrown = await Assert.ThrowsAsync<ChapterNotFoundException>(
				() => retriever.GetParagraphsAsync("novel/x/chapter-999999"));

			Assert.Contains("not a chapter", thrown.Message);
		}

		[Fact]
		public async Task The_notice_is_recognised_whatever_its_casing()
		{
			ParagraphsRetriever retriever = Build(Page("SOME NOVEL PAGES MOVED FOR BETTER USER EXPERIENCE."));

			await Assert.ThrowsAsync<ChapterNotFoundException>(
				() => retriever.GetParagraphsAsync("novel/x/chapter-1"));
		}

		[Fact]
		public async Task A_genuinely_short_chapter_is_still_a_chapter()
		{
			// Two paragraphs and no marker phrase: short, but real.
			ParagraphsRetriever retriever = Build(Page("Chapter Twelve", "It ended there."));

			Dictionary<int, string> paragraphs = await retriever.GetParagraphsAsync("novel/x/chapter-12");

			Assert.Equal(2, paragraphs.Count);
		}

		[Fact]
		public async Task A_long_page_is_never_taken_for_the_notice()
		{
			// The marker phrase appearing inside a real chapter must not disqualify it.
			ParagraphsRetriever retriever = Build(Page(
				"Chapter Nine",
				"He told them to use the search function, and laughed.",
				"Third.",
				"Fourth."));

			Dictionary<int, string> paragraphs = await retriever.GetParagraphsAsync("novel/x/chapter-9");

			Assert.Equal(4, paragraphs.Count);
		}

		[Fact]
		public async Task A_page_with_no_content_div_is_reported()
		{
			ParagraphsRetriever retriever = Build("<html><body><p>elsewhere</p></body></html>");

			ChapterUnavailableException thrown = await Assert.ThrowsAsync<ChapterUnavailableException>(
				() => retriever.GetParagraphsAsync("novel/x/chapter-1"));

			Assert.Contains("No paragraphs found", thrown.Message);
		}

		[Theory]
		[InlineData(HttpStatusCode.TooManyRequests)]
		[InlineData(HttpStatusCode.BadGateway)]
		[InlineData(HttpStatusCode.Found)]
		[InlineData(HttpStatusCode.MovedPermanently)]
		public async Task A_refusal_an_error_or_a_redirect_is_worth_asking_again(HttpStatusCode status)
		{
			// The redirect is the site's random one to another site's "Loading..." page.
			ParagraphsRetriever retriever = Build(Page("nope"), status);

			await Assert.ThrowsAsync<ChapterUnavailableException>(
				() => retriever.GetParagraphsAsync("novel/x/chapter-52"));
		}

		[Fact]
		public async Task The_not_found_page_is_a_chapter_that_does_not_exist()
		{
			// What the site answers, with 200, for one past the last chapter.
			ParagraphsRetriever retriever = Build(
				"<html><head><title>Page Chapter Not Found</title></head><body><div id='content'>"
				+ "<p>Novel Chapters</p><p>Latest Novels</p><p>Completed Novels</p><p>Ranking</p></div></body></html>");

			await Assert.ThrowsAsync<ChapterNotFoundException>(
				() => retriever.GetParagraphsAsync("novel/x/chapter-2335"));
		}

		[Fact]
		public async Task A_404_is_a_chapter_that_does_not_exist()
		{
			ParagraphsRetriever retriever = Build("", HttpStatusCode.NotFound);

			await Assert.ThrowsAsync<ChapterNotFoundException>(
				() => retriever.GetParagraphsAsync("novel/x/chapter-2335"));
		}
	}
}
