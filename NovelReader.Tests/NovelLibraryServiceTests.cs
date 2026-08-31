using NovelReader.Domain.RealTimeReader.Parsing;
using NovelReader.Domain.RealTimeReader.User;

namespace NovelReader.Tests
{
	/// <summary>
	/// Catalogue details cached beside the bookmark and refreshed once a day (D22).
	/// </summary>
	public class NovelLibraryServiceTests
	{
		private const string User = "anton";

		private static readonly NovelDataDto Reverend = new("Reverend Insanity", "reverend-insanity", 2, 3000);
		private static readonly NovelDataDto Shadow = new("Shadow Slave", "shadow-slave", 1, 3168);

		private sealed record Harness(
			NovelLibraryService Service,
			InMemoryReadingProgressStore Store,
			StubSearchNovelsRetriever Catalogue,
			DeferredBackgroundWorkScheduler Scheduler);

		private static Harness Build(params NovelDataDto[] catalogue)
		{
			InMemoryReadingProgressStore store = new();
			StubSearchNovelsRetriever retriever = new(catalogue.Length > 0 ? catalogue : [Reverend, Shadow]);
			DeferredBackgroundWorkScheduler scheduler = new();

			return new Harness(new NovelLibraryService(store, retriever, scheduler), store, retriever, scheduler);
		}

		// ---- When details count as stale ----

		[Fact]
		public void A_novel_never_looked_up_is_stale()
		{
			NovelSummary novel = new() { Slug = "reverend-insanity" };

			Assert.True(NovelLibraryService.IsStale(novel, DateTime.UtcNow));
		}

		[Theory]
		[InlineData(0, false)]
		[InlineData(23, false)]
		[InlineData(24, true)]
		[InlineData(25, true)]
		[InlineData(240, true)]
		public void Details_go_stale_after_a_day(int hoursAgo, bool expected)
		{
			DateTime now = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);
			NovelSummary novel = new()
			{
				Slug = "reverend-insanity",
				Title = "Reverend Insanity",
				CheckedAtUtc = now.AddHours(-hoursAgo)
			};

			Assert.Equal(expected, NovelLibraryService.IsStale(novel, now));
		}

		// ---- What opening the library does ----

		[Fact]
		public async Task Fresh_details_are_returned_without_asking_the_catalogue()
		{
			Harness harness = Build();
			harness.Store.Library.Add(new NovelSummary
			{
				Slug = "reverend-insanity",
				Title = "Reverend Insanity",
				Rank = 2,
				TotalChapters = 3000,
				CheckedAtUtc = DateTime.UtcNow.AddHours(-1)
			});

			IReadOnlyList<NovelSummary> library = await harness.Service.GetLibraryAsync(User);

			Assert.Single(library);
			Assert.Equal("Reverend Insanity", library[0].Title);
			Assert.Equal(0, harness.Scheduler.ScheduledCount);
		}

		[Fact]
		public async Task Stale_details_are_refreshed_in_the_background()
		{
			Harness harness = Build();
			harness.Store.Library.Add(new NovelSummary
			{
				Slug = "reverend-insanity",
				CheckedAtUtc = DateTime.UtcNow.AddHours(-30)
			});

			IReadOnlyList<NovelSummary> library = await harness.Service.GetLibraryAsync(User);

			// The stored value comes back straight away; the lookup happens behind it.
			Assert.Single(library);
			Assert.Equal(1, harness.Scheduler.ScheduledCount);
			Assert.Equal(0, harness.Catalogue.RequestedPaths.Count);

			await harness.Scheduler.RunQueuedAsync();

			Assert.Single(harness.Store.DetailWrites);
			Assert.Equal("Reverend Insanity", harness.Store.DetailWrites[0].Summary?.Title);
			Assert.Equal(3000, harness.Store.DetailWrites[0].Summary?.TotalChapters);
			Assert.Equal(2, harness.Store.DetailWrites[0].Summary?.Rank);
		}

		[Fact]
		public async Task Opening_the_library_twice_schedules_one_refresh_per_novel()
		{
			Harness harness = Build();
			harness.Store.Library.Add(new NovelSummary { Slug = "reverend-insanity" });
			harness.Store.Library.Add(new NovelSummary { Slug = "shadow-slave" });

			await harness.Service.GetLibraryAsync(User);
			await harness.Service.GetLibraryAsync(User);
			await harness.Service.GetLibraryAsync(User);

			// One per novel, not one per page load (D17).
			Assert.Equal(2, harness.Scheduler.ScheduledCount);
		}

		// ---- The lookup itself ----

		[Fact]
		public async Task Only_the_matching_slug_is_taken_from_the_results()
		{
			Harness harness = Build();

			// The catalogue answers with both novels; only ours is this novel.
			await harness.Service.RefreshAsync(User, new NovelSummary { Slug = "shadow-slave" });

			Assert.Equal("Shadow Slave", harness.Store.DetailWrites[0].Summary?.Title);
			Assert.Equal(3168, harness.Store.DetailWrites[0].Summary?.TotalChapters);
		}

		[Fact]
		public async Task A_novel_the_catalogue_does_not_know_is_still_stamped()
		{
			Harness harness = Build();

			await harness.Service.RefreshAsync(User, new NovelSummary { Slug = "not-in-the-catalogue" });

			// Nothing found, but the attempt is recorded — otherwise it would be retried on
			// every single request instead of tomorrow.
			(string novel, NovelSummary? summary, DateTime checkedAt) = harness.Store.DetailWrites.Single();
			Assert.Equal("not-in-the-catalogue", novel);
			Assert.Null(summary);
			Assert.True(checkedAt > DateTime.UtcNow.AddMinutes(-1));
		}

		[Fact]
		public async Task A_novel_with_no_title_is_searched_by_its_slug()
		{
			Harness harness = Build();

			await harness.Service.RefreshAsync(User, new NovelSummary { Slug = "reverend-insanity" });

			Assert.Equal("ajax/searchLive?keyword=reverend%20insanity", harness.Catalogue.RequestedPaths.Single());
		}

		[Fact]
		public async Task A_novel_with_a_known_title_is_searched_by_that()
		{
			Harness harness = Build();

			await harness.Service.RefreshAsync(User, new NovelSummary
			{
				Slug = "reverend-insanity",
				Title = "Reverend Insanity"
			});

			Assert.Equal("ajax/searchLive?keyword=Reverend%20Insanity", harness.Catalogue.RequestedPaths.Single());
		}

		[Fact]
		public async Task A_catalogue_that_is_down_still_stamps_the_attempt()
		{
			InMemoryReadingProgressStore store = new();
			DeferredBackgroundWorkScheduler scheduler = new();
			NovelLibraryService service = new(store, new FailingSearchNovelsRetriever(), scheduler);

			await service.RefreshAsync(User, new NovelSummary { Slug = "reverend-insanity" });

			// Otherwise the outage turns into a lookup on every single page load (D17).
			(string novel, NovelSummary? summary, DateTime checkedAt) = store.DetailWrites.Single();
			Assert.Equal("reverend-insanity", novel);
			Assert.Null(summary);
			Assert.True(checkedAt > DateTime.UtcNow.AddMinutes(-1));
		}

		[Fact]
		public async Task An_outage_does_not_take_the_page_down_with_it()
		{
			InMemoryReadingProgressStore store = new();
			DeferredBackgroundWorkScheduler scheduler = new();
			NovelLibraryService service = new(store, new FailingSearchNovelsRetriever(), scheduler);
			store.Library.Add(new NovelSummary { Slug = "reverend-insanity", Title = "Reverend Insanity" });

			IReadOnlyList<NovelSummary> library = await service.GetLibraryAsync(User);

			// The stored name is still what the reader sees.
			Assert.Equal("Reverend Insanity", library.Single().Title);
			await scheduler.RunQueuedAsync();
		}

		[Fact]
		public async Task The_search_is_asked_once_per_refresh_not_once_per_result()
		{
			Harness harness = Build();
			harness.Store.Library.Add(new NovelSummary { Slug = "reverend-insanity" });

			await harness.Service.GetLibraryAsync(User);
			await harness.Scheduler.RunQueuedAsync();

			Assert.Single(harness.Catalogue.RequestedPaths);
		}
	}

	public class NovelSearchQueryTests
	{
		[Fact]
		public void A_keyword_is_encoded_into_the_path()
		{
			Assert.Equal("ajax/searchLive?keyword=shadow%20slave", NovelSearchQuery.PathFor("shadow slave"));
		}

		[Fact]
		public void Characters_that_would_break_the_query_are_escaped()
		{
			string path = NovelSearchQuery.PathFor("a&b=c?d");

			Assert.DoesNotContain("&b", path);
			Assert.DoesNotContain("=c", path);
			Assert.StartsWith("ajax/searchLive?keyword=", path);
		}

		[Fact]
		public void A_slug_becomes_the_words_it_was_made_from()
		{
			Assert.Equal("reverend insanity", NovelSearchQuery.KeywordFromSlug("reverend-insanity"));
			Assert.Equal("shadow slave", NovelSearchQuery.KeywordFromSlug("shadow-slave"));
		}
	}
}
