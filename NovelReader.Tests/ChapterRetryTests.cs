using NovelReader.Domain.RealTimeReader.Parsing;
using NovelReader.Domain.RealTimeReader.Reading;

namespace NovelReader.Tests
{
	/// <summary>A chapter the site did not serve is asked for again; one it does not have is not (D49).</summary>
	public class ChapterRetryTests
	{
		private static ChapterPreparationService Service(IParagraphsRetriever retriever) =>
			new(new FakeNovelRepository(new FakeCollectionOfChapters()), retriever, new EmptyVocabularyRepository())
			{
				RetryDelays = [TimeSpan.Zero, TimeSpan.Zero]
			};

		[Fact]
		public async Task A_redirect_or_an_empty_page_is_retried_until_the_chapter_comes()
		{
			ScriptedRetriever retriever = new(
				new ChapterUnavailableException("redirected"),
				new HttpRequestException("connection reset"));

			PreparedChapter chapter = await Service(retriever).PrepareAsync("anton", "reverend-insanity", 52);

			Assert.Equal(3, retriever.Calls);
			Assert.Equal(2, chapter.Paragraphs.Count);
		}

		[Fact]
		public async Task Retrying_stops_after_the_last_wait()
		{
			ScriptedRetriever retriever = new(
				new ChapterUnavailableException("redirected"),
				new ChapterUnavailableException("redirected"),
				new ChapterUnavailableException("redirected"));

			await Assert.ThrowsAsync<ChapterUnavailableException>(
				() => Service(retriever).PrepareAsync("anton", "reverend-insanity", 52));

			Assert.Equal(3, retriever.Calls);
		}

		[Fact]
		public async Task A_chapter_the_site_does_not_have_is_asked_for_once()
		{
			ScriptedRetriever retriever = new(new ChapterNotFoundException("past the end"));

			await Assert.ThrowsAsync<ChapterNotFoundException>(
				() => Service(retriever).PrepareAsync("anton", "reverend-insanity", 2335));

			Assert.Equal(1, retriever.Calls);
		}

		/// <summary>Throws the given failures in order, then serves a two-paragraph chapter.</summary>
		private sealed class ScriptedRetriever(params Exception[] failures) : IParagraphsRetriever
		{
			public int Calls { get; private set; }

			public Task<Dictionary<int, string>> GetParagraphsAsync(string uriPath)
			{
				Calls++;
				return Calls <= failures.Length
					? Task.FromException<Dictionary<int, string>>(failures[Calls - 1])
					: Task.FromResult(new Dictionary<int, string> { [1] = "Chapter 52", [2] = "It went on." });
			}
		}
	}
}
