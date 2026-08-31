using NovelReader.Domain.RealTimeReader.Reading;

namespace NovelReader.Tests
{
	/// <summary>
	/// The prefetch storm (D17): serving a chapter asks for the next one to be warmed, and
	/// those requests used to pile up because their cache checks all ran before any of them
	/// stored anything.
	/// </summary>
	public class ChapterReaderConcurrencyTests
	{
		private const string User = "anton";
		private const string Novel = "reverend-insanity";

		private sealed record Harness(
			ChapterReader Reader,
			CountingParagraphsRetriever Retriever,
			DeferredBackgroundWorkScheduler Scheduler);

		private static Harness Build()
		{
			CountingParagraphsRetriever retriever = new();
			ChapterPreparationService preparation = new(
				new FakeNovelRepository(new FakeCollectionOfChapters()),
				retriever,
				new EmptyVocabularyRepository());

			DeferredBackgroundWorkScheduler scheduler = new();
			ChapterReader reader = new(
				preparation,
				new InMemoryPreparedChapterCache(),
				scheduler,
				new UserRequestGate());

			return new Harness(reader, retriever, scheduler);
		}

		[Fact]
		public async Task Loading_a_chapter_returns_all_of_its_paragraphs()
		{
			Harness harness = Build();

			PreparedChapter chapter = await harness.Reader.LoadChapterAsync(User, Novel, 1);

			Assert.Equal(10, chapter.Paragraphs.Count);
			Assert.Equal(1, chapter.ChapterNumber);
			Assert.True(chapter.TryGetParagraph(1, out string first));
			Assert.Contains("chapter-1", first);
		}

		[Fact]
		public async Task Re_reading_a_chapter_queues_one_prefetch_not_one_per_request()
		{
			Harness harness = Build();

			for (int attempt = 0; attempt < 10; attempt++)
			{
				await harness.Reader.LoadChapterAsync(User, Novel, 1);
			}

			Assert.Equal(1, harness.Scheduler.ScheduledCount);
			Assert.All(harness.Scheduler.Descriptions, description => Assert.Contains("chapter 2", description));
		}

		[Fact]
		public async Task The_next_chapter_is_scraped_once_however_often_this_one_is_read()
		{
			Harness harness = Build();

			for (int attempt = 0; attempt < 10; attempt++)
			{
				await harness.Reader.LoadChapterAsync(User, Novel, 1);
			}

			await harness.Scheduler.RunQueuedAsync();

			Assert.Equal(1, harness.Retriever.CallsForChapter(2));
			// Chapter 1 read once, chapter 2 prefetched once.
			Assert.Equal(2, harness.Retriever.TotalCalls);
		}

		[Fact]
		public async Task Concurrent_requests_from_one_reader_do_not_scrape_the_same_chapter_twice()
		{
			Harness harness = Build();

			await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
				harness.Reader.LoadChapterAsync(User, Novel, 1)));

			Assert.Equal(1, harness.Retriever.CallsForChapter(1));
			Assert.Equal(1, harness.Scheduler.ScheduledCount);
		}

		[Fact]
		public async Task A_finished_prefetch_is_not_repeated_because_the_chapter_is_now_stored()
		{
			Harness harness = Build();

			await harness.Reader.LoadChapterAsync(User, Novel, 1);
			await harness.Scheduler.RunQueuedAsync();

			// Reading on releases the in-flight key, so the durable tier is the guard now.
			for (int attempt = 0; attempt < 9; attempt++)
			{
				await harness.Reader.LoadChapterAsync(User, Novel, 1);
			}

			await harness.Scheduler.RunQueuedAsync();

			Assert.Equal(1, harness.Retriever.CallsForChapter(2));
		}

		[Fact]
		public async Task Reading_two_chapters_prefetches_each_successor_once()
		{
			Harness harness = Build();

			await harness.Reader.LoadChapterAsync(User, Novel, 1);
			await harness.Reader.LoadChapterAsync(User, Novel, 2);

			Assert.Equal(2, harness.Scheduler.ScheduledCount);
		}
	}
}
