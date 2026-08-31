using System.Collections.Concurrent;

namespace NovelReader.Domain.RealTimeReader.Reading
{
	/// <summary>
	/// Serves whole prepared (marked-up) chapters, and keeps the chapter ahead warm so the
	/// reader never waits for a scrape.
	///
	/// This used to hand back one paragraph at a time, which meant a request per paragraph
	/// and a roll-forward loop for "that paragraph is past the end of the chapter". Resuming
	/// where a reader left off needs a whole chapter on screen anyway, so the unit of work is
	/// now the chapter (D19).
	/// </summary>
	public class ChapterReader(
		ChapterPreparationService chapterPreparationService,
		IPreparedChapterCache preparedChapterCache,
		IBackgroundWorkScheduler backgroundWorkScheduler,
		UserRequestGate userRequestGate)
	{
		/// <summary>
		/// Prefetches already running, keyed user/novel/chapter. Every chapter served asks for
		/// the next one to be warmed, and without this those requests pile up (D17).
		/// </summary>
		private readonly ConcurrentDictionary<string, byte> prefetchesInFlight = new(StringComparer.Ordinal);

		/// <summary>
		/// The whole chapter, marked up for this reader. Takes the reader's turn, so one
		/// reader is served one chapter at a time (D17).
		/// </summary>
		public async Task<PreparedChapter> LoadChapterAsync(
			string userName,
			string novelName,
			int chapterNumber,
			CancellationToken cancellationToken = default)
		{
			using IDisposable turn = await userRequestGate.AcquireAsync(userName, cancellationToken);

			return await GetPreparedChapterAsync(userName, novelName, chapterNumber, cancellationToken);
		}

		/// <summary>
		/// Memory tier, then the durable tier, then prepare it here and now. Reaching a
		/// chapter that was waiting in the durable tier promotes it into memory and frees
		/// that slot for the next one (D8).
		/// </summary>
		private async Task<PreparedChapter> GetPreparedChapterAsync(
			string userName,
			string novelName,
			int chapterNumber,
			CancellationToken cancellationToken)
		{
			PreparedChapter? fromMemory = await preparedChapterCache.TryGetFromMemoryAsync(
				userName, novelName, chapterNumber, cancellationToken);
			if (fromMemory is not null)
			{
				SchedulePrefetch(userName, novelName, chapterNumber + 1);
				return fromMemory;
			}

			PreparedChapter? fromDurable = await preparedChapterCache.TryGetFromDurableAsync(
				userName, novelName, chapterNumber, cancellationToken);
			if (fromDurable is not null)
			{
				await preparedChapterCache.PromoteToMemoryAsync(userName, fromDurable, cancellationToken);
				SchedulePrefetch(userName, novelName, chapterNumber + 1);
				return fromDurable;
			}

			// First time on this chapter: prepare it now, and the next one in the background.
			PreparedChapter prepared = await chapterPreparationService.PrepareAsync(
				userName, novelName, chapterNumber, cancellationToken);

			await preparedChapterCache.PromoteToMemoryAsync(userName, prepared, cancellationToken);
			SchedulePrefetch(userName, novelName, chapterNumber + 1);
			return prepared;
		}

		private void SchedulePrefetch(string userName, string novelName, int chapterNumber)
		{
			// The cache checks below race each other, so they cannot be the guard: several
			// prefetches for one chapter would all look and all find nothing. Claiming the
			// key first is what makes it exactly one.
			string key = $"{userName}\u001f{novelName}\u001f{chapterNumber}";
			if (!prefetchesInFlight.TryAdd(key, 0))
			{
				return;
			}

			try
			{
				backgroundWorkScheduler.Schedule(async cancellationToken =>
				{
					try
					{
						PreparedChapter? alreadyHot = await preparedChapterCache.TryGetFromMemoryAsync(
							userName, novelName, chapterNumber, cancellationToken);
						if (alreadyHot is not null)
						{
							return;
						}

						PreparedChapter? alreadyStored = await preparedChapterCache.TryGetFromDurableAsync(
							userName, novelName, chapterNumber, cancellationToken);
						if (alreadyStored is not null)
						{
							return;
						}

						PreparedChapter prepared = await chapterPreparationService.PrepareAsync(
							userName, novelName, chapterNumber, cancellationToken);

						await preparedChapterCache.StoreInDurableAsync(userName, prepared, cancellationToken);
					}
					finally
					{
						prefetchesInFlight.TryRemove(key, out _);
					}
				}, $"prefetch chapter {chapterNumber} of '{novelName}' for {userName}");
			}
			catch
			{
				// Never let a scheduler failure leave the key claimed; that would block every
				// later prefetch of this chapter for the lifetime of the process.
				prefetchesInFlight.TryRemove(key, out _);
				throw;
			}
		}
	}
}
