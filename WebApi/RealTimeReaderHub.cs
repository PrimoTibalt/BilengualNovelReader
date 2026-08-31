using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NovelReader.Domain.RealTimeReader.Definitions;
using NovelReader.Domain.RealTimeReader.Parsing;
using NovelReader.Domain.RealTimeReader.Reading;
using NovelReader.Domain.RealTimeReader.User;
using NovelReader.Domain.RealTimeReader.Vocabulary;

namespace NovelReader
{
	/// <summary>
	/// The reading page's whole server API.
	///
	/// No method takes a user name. The reader is whoever the authentication cookie says they
	/// are (<see cref="CallerName"/>) — a client that could name itself could read and rewrite
	/// anyone's progress and vocabulary (D20).
	/// </summary>
	[Authorize]
	public class RealTimeReaderHub(
		ChapterReader chapterReader,
		IReadingProgressStore readingProgressStore,
		NovelLibraryService novelLibraryService,
		ISearchNovelsRetriever searchNovelsRetriever,
		DefinitionLookupService definitionLookupService,
		IVocabularyRepository vocabularyRepository,
		IPreparedChapterCache preparedChapterCache,
		ILogger<RealTimeReaderHub> logger) : Hub
	{
		/// <summary>Where a reader with no history starts.</summary>
		internal const string DefaultNovelName = "reverend-insanity";
		internal const int DefaultChapterNumber = 1;

		private string CallerName =>
			Context.User?.Identity?.Name
			?? throw new HubException("Not signed in.");

		/// <summary>
		/// The first call the page makes. Answers with the reader's novels — for the
		/// navigation menu — and the position to restore.
		/// </summary>
		public async Task<ReadingSessionResponse> GetReadingSession()
		{
			string userName = CallerName;

			// Returns what is stored and refreshes anything stale in the background (D22).
			IReadOnlyList<NovelSummary> novels = await novelLibraryService.GetLibraryAsync(userName, Context.ConnectionAborted);
			ReadingProgress? mostRecent = await readingProgressStore.GetMostRecentAsync(userName, Context.ConnectionAborted);

			return new ReadingSessionResponse(
				userName,
				[.. novels.Select(novel => new NovelSummaryResponse(novel.Slug, novel.Title, novel.Rank, novel.TotalChapters))],
				mostRecent?.NovelName ?? DefaultNovelName,
				mostRecent?.ChapterNumber ?? DefaultChapterNumber,
				mostRecent?.ParagraphNumber ?? 1,
				mostRecent is not null);
		}

		/// <summary>
		/// Shortest query worth sending on. One or two letters match most of the catalogue,
		/// so the answer would be noise and the request wasted.
		/// </summary>
		internal const int MinimumSearchLength = 2;

		/// <summary>
		/// Searches the source site's catalogue. The page debounces typing, so this runs once
		/// the reader has stopped — not per keystroke.
		///
		/// Deliberately outside the reading gate (D17): a search must not queue behind a
		/// chapter load, nor hold up the next one.
		/// </summary>
		public async Task<IReadOnlyList<NovelSearchResponse>> SearchNovels(string query)
		{
			string userName = CallerName;
			string trimmed = (query ?? string.Empty).Trim();

			if (trimmed.Length < MinimumSearchLength)
			{
				return [];
			}

			try
			{
				IReadOnlyCollection<NovelDataDto> found = await searchNovelsRetriever.GetNovelsAsync(
					NovelSearchQuery.PathFor(trimmed));

				List<NovelSearchResponse> results = new(found.Count);
				foreach (NovelDataDto novel in found)
				{
					results.Add(new NovelSearchResponse(novel.Title, novel.Slug, novel.Rank, novel.TotalChapter));
				}

				return results;
			}
			catch (Exception exception)
			{
				// A search that fails is an empty result list, not a broken reading session.
				logger.LogWarning(exception, "Novel search failed for {User} on {Query}", userName, trimmed);
				return [];
			}
		}

		/// <summary>
		/// Where this reader left off in one novel, so opening it from the library menu
		/// resumes rather than restarting.
		/// </summary>
		public async Task<ReadingSessionResponse> GetNovelProgress(string novelName)
		{
			string userName = CallerName;
			ReadingProgress? progress = string.IsNullOrWhiteSpace(novelName)
				? null
				: await readingProgressStore.GetAsync(userName, novelName, Context.ConnectionAborted);

			return new ReadingSessionResponse(
				userName,
				[],
				novelName,
				progress?.ChapterNumber ?? DefaultChapterNumber,
				progress?.ParagraphNumber ?? 1,
				progress is not null);
		}

		/// <summary>
		/// A whole chapter, marked up for this reader. An empty chapter comes back with
		/// Found = false rather than an exception — the source site answers 200 with an empty
		/// page often enough that it is a normal outcome, not a fault.
		/// </summary>
		public async Task<ChapterResponse> LoadChapter(string novelName, int chapterNumber)
		{
			string userName = CallerName;

			if (string.IsNullOrWhiteSpace(novelName) || chapterNumber < 1)
			{
				return new ChapterResponse(novelName ?? string.Empty, chapterNumber, [], false);
			}

			List<ParagraphResponse> paragraphs = [];
			try
			{
				PreparedChapter chapter = await chapterReader.LoadChapterAsync(
					userName, novelName, chapterNumber, Context.ConnectionAborted);

				foreach ((int number, string markup) in chapter.Paragraphs.OrderBy(pair => pair.Key))
				{
					paragraphs.Add(new ParagraphResponse(number, markup));
				}
			}
			catch (Exception exception)
			{
				// A chapter that will not load must not take the connection down with it.
				logger.LogWarning(exception, "Could not load chapter {Chapter} of {Novel}", chapterNumber, novelName);
			}

			return new ChapterResponse(novelName, chapterNumber, paragraphs, paragraphs.Count > 0);
		}

		/// <summary>
		/// The reader's bookmark: the last paragraph they actually had on screen. The page
		/// sends this once the reader has stopped scrolling, not on every scroll event.
		/// </summary>
		public async Task ReportProgress(string novelName, int chapterNumber, int paragraphNumber)
		{
			if (string.IsNullOrWhiteSpace(novelName) || chapterNumber < 1 || paragraphNumber < 1)
			{
				return;
			}

			await readingProgressStore.SaveAsync(CallerName, new ReadingProgress
			{
				NovelName = novelName,
				ChapterNumber = chapterNumber,
				ParagraphNumber = paragraphNumber,
				UpdatedAtUtc = DateTime.UtcNow
			}, Context.ConnectionAborted);
		}

		/// <summary>
		/// Looks a selection up. Always answers — a word with no definition comes back with
		/// Found = false rather than silence, so the box can say so.
		/// </summary>
		public async Task GetDefinition(string surfaceForm)
		{
			string userName = CallerName;

			string normalizedTerm = TermNormalizer.Normalize(surfaceForm);
			if (normalizedTerm.Length == 0)
			{
				return;
			}

			bool isSaved = await vocabularyRepository.ContainsAsync(userName, normalizedTerm, Context.ConnectionAborted);

			WordDefinition? definition = null;
			try
			{
				definition = await definitionLookupService.LookUpAsync(normalizedTerm, Context.ConnectionAborted);
			}
			catch (Exception exception)
			{
				// A dictionary outage must not break the reading session.
				logger.LogWarning(exception, "Definition lookup failed for {Term}", normalizedTerm);
			}

			List<DefinitionSenseResponse> senses = [];
			if (definition is not null)
			{
				foreach (DefinitionSense sense in definition.Senses)
				{
					senses.Add(new DefinitionSenseResponse(sense.PartOfSpeech, sense.Text, sense.Example));
				}
			}

			await Clients.Caller.SendAsync("ReturnDefinition", new DefinitionResponse(
				normalizedTerm,
				surfaceForm,
				senses,
				definition?.SourceName,
				definition?.SourceUrl,
				isSaved,
				senses.Count > 0));
		}

		/// <summary>
		/// Answers the reading page's <c>t</c> key with a hard-coded translation. The language
		/// pair and the choice of word-vs-sentence are still open (D10), so this returns a
		/// clearly-labelled stub rather than guessing.
		/// </summary>
		public async Task Translate(string surfaceForm)
		{
			string userName = CallerName;

			string normalizedTerm = TermNormalizer.Normalize(surfaceForm);
			if (normalizedTerm.Length == 0)
			{
				return;
			}

			logger.LogDebug("Translation requested by {User} for {Term}; answering with the stub", userName, normalizedTerm);

			await Clients.Caller.SendAsync("ReturnTranslation", TranslationResponse.Stub(normalizedTerm));
		}

		public async Task SaveWord(string novelName, string surfaceForm)
		{
			string userName = CallerName;

			string normalizedTerm = TermNormalizer.Normalize(surfaceForm);
			if (normalizedTerm.Length == 0)
			{
				return;
			}

			await vocabularyRepository.AddAsync(userName, new VocabularyEntry
			{
				NormalizedTerm = normalizedTerm,
				SurfaceForm = surfaceForm.Trim(),
				NovelName = novelName,
				SavedAtUtc = DateTime.UtcNow
			}, Context.ConnectionAborted);

			// Underlines are baked into cached markup, so those chapters are now stale (F5).
			await preparedChapterCache.InvalidateForUserAsync(userName, Context.ConnectionAborted);

			await Clients.Caller.SendAsync("ReturnVocabularyChanged", new VocabularyChangedResponse(normalizedTerm, true));
		}

		public async Task DeleteWord(string surfaceForm)
		{
			string userName = CallerName;

			string normalizedTerm = TermNormalizer.Normalize(surfaceForm);
			if (normalizedTerm.Length == 0)
			{
				return;
			}

			await vocabularyRepository.RemoveAsync(userName, normalizedTerm, Context.ConnectionAborted);
			await preparedChapterCache.InvalidateForUserAsync(userName, Context.ConnectionAborted);

			await Clients.Caller.SendAsync("ReturnVocabularyChanged", new VocabularyChangedResponse(normalizedTerm, false));
		}
	}
}
