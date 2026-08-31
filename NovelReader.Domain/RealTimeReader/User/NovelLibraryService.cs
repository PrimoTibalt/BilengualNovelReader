using System.Collections.Concurrent;
using NovelReader.Domain.RealTimeReader.Parsing;
using NovelReader.Domain.RealTimeReader.Reading;

namespace NovelReader.Domain.RealTimeReader.User
{
	/// <summary>
	/// The reader's library: their novels, with the catalogue's name, rank and chapter count
	/// cached beside each bookmark and refreshed once a day (D22).
	///
	/// The refresh goes through the search endpoint rather than the novel's own page: search
	/// answers JSON, while the page would have to be fetched and parsed as HTML for the same
	/// three fields.
	/// </summary>
	public sealed class NovelLibraryService(
		IReadingProgressStore readingProgressStore,
		ISearchNovelsRetriever searchNovelsRetriever,
		IBackgroundWorkScheduler backgroundWorkScheduler)
	{
		/// <summary>How long cached catalogue details are trusted before being looked up again.</summary>
		public static readonly TimeSpan DetailsLifetime = TimeSpan.FromHours(24);

		/// <summary>
		/// Refreshes already running, keyed user/novel. Opening the page schedules one per stale
		/// novel, and two page loads in quick succession would otherwise schedule each twice (D17).
		/// </summary>
		private readonly ConcurrentDictionary<string, byte> refreshesInFlight = new(StringComparer.Ordinal);

		/// <summary>
		/// The library as it stands, plus a background refresh for anything stale. Deliberately
		/// returns what is stored rather than waiting: the reading page asks for this before it
		/// can show anything, and a day-old rank is not worth delaying the first paragraph for.
		/// </summary>
		public async Task<IReadOnlyList<NovelSummary>> GetLibraryAsync(
			string userName,
			CancellationToken cancellationToken = default)
		{
			IReadOnlyList<NovelSummary> novels = await readingProgressStore.GetNovelsReadAsync(userName, cancellationToken);

			DateTime now = DateTime.UtcNow;
			foreach (NovelSummary novel in novels)
			{
				if (IsStale(novel, now))
				{
					ScheduleRefresh(userName, novel);
				}
			}

			return novels;
		}

		/// <summary>
		/// Never looked up, or looked up more than <see cref="DetailsLifetime"/> ago. A novel
		/// whose lookup found nothing still counts as looked up, so it is retried tomorrow rather
		/// than on every request.
		/// </summary>
		public static bool IsStale(NovelSummary novel, DateTime nowUtc)
		{
			return novel.CheckedAtUtc is not DateTime checkedAt
				|| nowUtc - checkedAt >= DetailsLifetime;
		}

		/// <summary>
		/// Looks one novel up in the catalogue and stores the result. Public so a test can drive
		/// it directly rather than only through the scheduler.
		/// </summary>
		public async Task RefreshAsync(
			string userName,
			NovelSummary novel,
			CancellationToken cancellationToken = default)
		{
			// The stored title is the catalogue's own wording, so it searches better than a
			// de-hyphenated slug; the slug is the fallback for a novel never looked up.
			string keyword = string.IsNullOrWhiteSpace(novel.Title)
				? NovelSearchQuery.KeywordFromSlug(novel.Slug)
				: novel.Title;

			NovelSummary? found = null;
			try
			{
				IReadOnlyCollection<NovelDataDto> results =
					await searchNovelsRetriever.GetNovelsAsync(NovelSearchQuery.PathFor(keyword));

				foreach (NovelDataDto candidate in results)
				{
					// Search returns everything matching the words; only our own slug is this novel.
					if (!string.Equals(candidate.Slug, novel.Slug, StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					found = new NovelSummary
					{
						Slug = novel.Slug,
						Title = candidate.Title,
						Rank = candidate.Rank,
						TotalChapters = candidate.TotalChapter
					};
					break;
				}
			}
			catch (Exception) when (!cancellationToken.IsCancellationRequested)
			{
				// A catalogue that is down, slow or answering nonsense still counts as asked.
				// Leaving the timestamp alone would mean looking it up again on the very next
				// page load, which is how the last request storm started (D17). Nothing is
				// overwritten, so a day-old name and rank stay on screen meanwhile.
			}

			// Written either way, and with null it moves only the timestamp: a novel the
			// catalogue cannot find is retried tomorrow rather than on the next request.
			await readingProgressStore.SaveNovelDetailsAsync(
				userName, novel.Slug, found, DateTime.UtcNow, cancellationToken);
		}

		private void ScheduleRefresh(string userName, NovelSummary novel)
		{
			string key = $"{userName}\u001f{novel.Slug}";
			if (!refreshesInFlight.TryAdd(key, 0))
			{
				return;
			}

			try
			{
				backgroundWorkScheduler.Schedule(async cancellationToken =>
				{
					try
					{
						await RefreshAsync(userName, novel, cancellationToken);
					}
					finally
					{
						refreshesInFlight.TryRemove(key, out _);
					}
				}, $"refresh catalogue details for '{novel.Slug}' of {userName}");
			}
			catch
			{
				// A scheduler failure must not leave the key claimed for the process's lifetime.
				refreshesInFlight.TryRemove(key, out _);
				throw;
			}
		}
	}
}
