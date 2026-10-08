using System.Collections.Concurrent;
using NovelReader.Domain.RealTimeReader.Definitions;
using NovelReader.Domain.RealTimeReader.Reading;

namespace NovelReader.Domain.RealTimeReader.Vocabulary
{
	/// <summary>
	/// Gives words saved before base forms existed the base their dictionary entry names (D39).
	///
	/// Without it they would be matched through the suffix rules' guesses forever, and those are
	/// the weak part: an `evening` saved last month would start underlining every "even". The
	/// definitions are almost all cached already (D2) — each word was looked up before it was
	/// saved — so this is mostly cache reads, done once per reader per process, in the
	/// background, never in the way of a chapter load.
	/// </summary>
	public sealed class VocabularyBaseForms(
		IVocabularyRepository vocabularyRepository,
		DefinitionLookupService dictionary,
		IPreparedChapterCache preparedChapterCache,
		IBackgroundWorkScheduler backgroundWorkScheduler)
	{
		/// <summary>Readers already brought up to date since the process started.</summary>
		private readonly ConcurrentDictionary<string, bool> _done = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Schedules the backfill for this reader, unless it has already run.</summary>
		public void EnsureFor(string userName)
		{
			if (!_done.TryAdd(userName, true)) return;

			backgroundWorkScheduler.Schedule(
				cancellationToken => BackfillAsync(userName, cancellationToken),
				$"base forms for {userName}");
		}

		/// <summary>Returns how many entries were given a base form.</summary>
		public async Task<int> BackfillAsync(string userName, CancellationToken cancellationToken = default)
		{
			IReadOnlyCollection<VocabularyEntry> entries =
				await vocabularyRepository.GetAllForUserAsync(userName, cancellationToken);

			int updated = 0;
			foreach (VocabularyEntry entry in entries.Where(entry => entry.BaseForm is null))
			{
				WordDefinition? definition;
				try
				{
					definition = await dictionary.LookUpAsync(entry.NormalizedTerm, cancellationToken);
				}
				catch (Exception) when (!cancellationToken.IsCancellationRequested)
				{
					// One word the dictionary could not answer for stays on the rules; the rest go on.
					continue;
				}

				string? baseForm = WordForms.BaseFromDictionary(entry.NormalizedTerm, definition);
				if (baseForm is null) continue;

				await vocabularyRepository.AddAsync(userName, new VocabularyEntry
				{
					NormalizedTerm = entry.NormalizedTerm,
					BaseForm = baseForm,
					SurfaceForm = entry.SurfaceForm,
					NovelName = entry.NovelName,
					SavedAtUtc = entry.SavedAtUtc
				}, cancellationToken);
				updated++;
			}

			// Chapters already prepared were marked up with the guesses.
			if (updated > 0)
			{
				await preparedChapterCache.InvalidateForUserAsync(userName, cancellationToken);
			}

			return updated;
		}
	}
}
