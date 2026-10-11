using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NovelReader.Domain.RealTimeReader.Accounts;
using NovelReader.Domain.RealTimeReader.Assistant;
using NovelReader.Domain.RealTimeReader.Definitions;
using NovelReader.Domain.RealTimeReader.Parsing;
using NovelReader.Domain.RealTimeReader.Reading;
using NovelReader.Domain.RealTimeReader.Translation;
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
		AssistedDefinitionService definitionService,
		DefinitionLookupService dictionary,
		VocabularyBaseForms vocabularyBaseForms,
		IVocabularyRepository vocabularyRepository,
		IPreparedChapterCache preparedChapterCache,
		TranslationService translationService,
		ITranslationSettingsStore translationSettings,
		AssistantSettingsService assistantSettings,
		AccountService accountService,
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

			// Words saved before base forms existed get theirs, in the background (D39).
			vocabularyBaseForms.EnsureFor(userName);

			// Returns what is stored and refreshes anything stale in the background (D22).
			IReadOnlyList<NovelSummary> novels = await novelLibraryService.GetLibraryAsync(userName, Context.ConnectionAborted);
			ReadingProgress? mostRecent = await readingProgressStore.GetMostRecentAsync(userName, Context.ConnectionAborted);
			TranslationSettings? settings = await translationSettings.GetAsync(userName, Context.ConnectionAborted);

			return new ReadingSessionResponse(
				userName,
				[.. novels.Select(novel => new NovelSummaryResponse(novel.Slug, novel.Title, novel.Rank, novel.TotalChapters))],
				mostRecent?.NovelName ?? DefaultNovelName,
				mostRecent?.ChapterNumber ?? DefaultChapterNumber,
				mostRecent?.ParagraphNumber ?? 1,
				mostRecent is not null,
				settings?.Email,
				settings?.TargetLanguage,
				[.. TranslationLanguages.All.Select(language => new TranslationLanguageResponse(language.Code, language.Name))],
				await DescribeAssistantAsync(userName));
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

			// Carried here too, cheaply, rather than sent as nulls: this shares a shape with the
			// opening session, and a response whose fields mean "unset" when they mean "not
			// looked up" is the kind of thing that is read wrongly later.
			TranslationSettings? settings = await translationSettings.GetAsync(userName, Context.ConnectionAborted);

			return new ReadingSessionResponse(
				userName,
				[],
				novelName,
				progress?.ChapterNumber ?? DefaultChapterNumber,
				progress?.ParagraphNumber ?? 1,
				progress is not null,
				settings?.Email,
				settings?.TargetLanguage,
				[.. TranslationLanguages.All.Select(language => new TranslationLanguageResponse(language.Code, language.Name))],
				await DescribeAssistantAsync(userName));
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
		///
		/// <paramref name="paragraph"/> is the text the selection was made in. The page sends it
		/// only when the reader has an AI set up, which reads the word against it (D35).
		/// </summary>
		public async Task GetDefinition(string surfaceForm, string? paragraph)
		{
			string userName = CallerName;

			string normalizedTerm = TermNormalizer.Normalize(surfaceForm);
			if (normalizedTerm.Length == 0)
			{
				return;
			}

			// Saved in any of its forms: with `run` kept, `running` is a saved word too (D39).
			bool isSaved = (await VocabularyFormsAsync(userName)).SavedTermFor(normalizedTerm) is not null;

			WordDefinition? definition = null;
			try
			{
				definition = await definitionService.LookUpAsync(userName, normalizedTerm, paragraph, Context.ConnectionAborted);
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
		/// Translates a selection into the reader's chosen language (D31).
		///
		/// <paramref name="email"/> and <paramref name="language"/> are normally null and the
		/// reader's stored settings are used. They are accepted because the first translation a
		/// reader ever asks for is sent at the same moment as the save that stores those
		/// settings: without carrying them, that one request would race the write and lose.
		///
		/// <paramref name="paragraph"/> is context for the reader's AI, as for a definition (D35).
		/// </summary>
		public async Task Translate(string surfaceForm, string? email, string? language, string? paragraph)
		{
			string userName = CallerName;

			string normalizedTerm = TermNormalizer.Normalize(surfaceForm);
			if (normalizedTerm.Length == 0)
			{
				return;
			}

			TranslationOutcome outcome;
			try
			{
				outcome = await translationService.TranslateAsync(
					userName, normalizedTerm, email, language, paragraph, Context.ConnectionAborted);
			}
			catch (Exception exception)
			{
				// A translation provider having a bad day is not a reason to break the session.
				logger.LogWarning(exception, "Translation failed for {Term}", normalizedTerm);
				outcome = TranslationOutcome.Failed(TranslationFailure.Unavailable);
			}

			TranslationResponse response = outcome.Failure switch
			{
				TranslationFailure.None => new TranslationResponse(
					normalizedTerm,
					surfaceForm,
					outcome.Translation!.Text,
					outcome.Translation.TargetLanguage,
					Error: null,
					outcome.Translation.ProviderName),
				TranslationFailure.NotConfigured =>
					TranslationResponse.Failed(normalizedTerm, surfaceForm, TranslationResponse.NotConfigured),
				TranslationFailure.SettingsInvalid =>
					TranslationResponse.Failed(normalizedTerm, surfaceForm, TranslationResponse.SettingsInvalid),
				_ => TranslationResponse.Failed(normalizedTerm, surfaceForm, TranslationResponse.Unavailable)
			};

			await Clients.Caller.SendAsync("ReturnTranslation", response);
		}

		/// <summary>
		/// Stores the reader's translation settings. The page checks both fields before it gets
		/// here, but the page is not the thing that decides — a client that skipped its own
		/// checks is held to the same rules (D31).
		/// </summary>
		public async Task<TranslationSettingsResponse> SaveTranslationSettings(string email, string language)
		{
			string userName = CallerName;

			SettingsFailure failure = TranslationSettingsValidator.Validate(email, language);
			if (failure != SettingsFailure.None)
			{
				return new TranslationSettingsResponse(
					Email: null,
					Language: null,
					failure == SettingsFailure.EmailInvalid
						? TranslationSettingsResponse.EmailInvalid
						: TranslationSettingsResponse.LanguageInvalid);
			}

			TranslationSettings settings = TranslationSettingsValidator.Normalize(email, language);
			await translationSettings.SaveAsync(userName, settings, Context.ConnectionAborted);

			logger.LogInformation("Translation settings stored for {User} ({Language})", userName, settings.TargetLanguage);

			return new TranslationSettingsResponse(settings.Email, settings.TargetLanguage, Error: null);
		}

		/// <summary>
		/// Stores the reader's AI: which service, which model, and the key to use (D35). An empty
		/// key keeps the stored one, so changing model does not mean pasting the key again.
		/// </summary>
		public async Task<AssistantSettingsResponse> SaveAssistantSettings(string serviceId, string model, string? apiKey)
		{
			string userName = CallerName;

			(AssistantSettingsSummary? saved, AssistantSettingsFailure failure) = await assistantSettings.SaveAsync(
				userName, serviceId, model, apiKey, Context.ConnectionAborted);

			if (failure != AssistantSettingsFailure.None)
			{
				AssistantSettingsResponse current = await DescribeAssistantAsync(userName);
				return current with
				{
					Error = failure switch
					{
						AssistantSettingsFailure.ServiceUnknown => AssistantSettingsResponse.ServiceUnknown,
						AssistantSettingsFailure.ModelInvalid => AssistantSettingsResponse.ModelInvalid,
						_ => AssistantSettingsResponse.KeyInvalid
					}
				};
			}

			logger.LogInformation("AI settings stored for {User} ({Service}, {Model})", userName, saved!.ServiceId, saved.Model);
			return AssistantResponse(saved);
		}

		/// <summary>Forgets the reader's AI, key included; the dictionaries answer again.</summary>
		public async Task<AssistantSettingsResponse> RemoveAssistantSettings()
		{
			string userName = CallerName;

			await assistantSettings.RemoveAsync(userName, Context.ConnectionAborted);
			logger.LogInformation("AI settings removed for {User}", userName);

			return AssistantResponse(null);
		}

		/// <summary>
		/// Sets the caller's password, once their current one checks out (D40). Like everything
		/// else here it acts on whoever the cookie names, so it cannot be pointed at another
		/// account (D20).
		/// </summary>
		public async Task<PasswordChangeResponse> ChangePassword(string currentPassword, string newPassword)
		{
			string userName = CallerName;

			AccountResult result = await accountService.ChangePasswordAsync(
				userName, currentPassword, newPassword, Context.ConnectionAborted);
			if (!result.Succeeded)
			{
				if (result.Failure == AccountFailure.CurrentPasswordWrong)
				{
					logger.LogWarning("Password change refused for {User}: wrong current password", userName);
				}

				if (result.Failure == AccountFailure.LockedOut)
				{
					logger.LogWarning("Password change locked for {User} ({Minutes} min left)", userName, result.RetryAfterMinutes);
					return new PasswordChangeResponse(PasswordChangeResponse.Locked, result.RetryAfterMinutes);
				}

				return new PasswordChangeResponse(result.Failure switch
				{
					AccountFailure.PasswordTooShort => PasswordChangeResponse.TooShort,
					AccountFailure.CurrentPasswordWrong => PasswordChangeResponse.CurrentWrong,
					_ => PasswordChangeResponse.Failed
				});
			}

			logger.LogInformation("Password changed for {User}", userName);
			return new PasswordChangeResponse(Error: null);
		}

		private async Task<AssistantSettingsResponse> DescribeAssistantAsync(string userName) =>
			AssistantResponse(await assistantSettings.DescribeAsync(userName, Context.ConnectionAborted));

		private static AssistantSettingsResponse AssistantResponse(AssistantSettingsSummary? summary) =>
			new(
				[.. AssistantServices.All.Select(service => new AssistantServiceResponse(service.Id, service.Name))],
				summary?.ServiceId,
				summary?.Model,
				summary?.KeyHint,
				Error: null);

		/// <summary>
		/// Keeps a word, with its base form worked out now (D39): the dictionary's "past tense of
		/// X" when it has one — usually cached, as the reader has just looked the word up — and
		/// the suffix rules, at match time, when it has no entry at all. The page is sent every form, so text already on
		/// screen is underlined the same way the server will mark up the next chapter.
		/// </summary>
		public async Task SaveWord(string novelName, string surfaceForm)
		{
			string userName = CallerName;

			string normalizedTerm = TermNormalizer.Normalize(surfaceForm);
			if (normalizedTerm.Length == 0)
			{
				return;
			}

			WordDefinition? definition = null;
			try
			{
				definition = await dictionary.LookUpAsync(normalizedTerm, Context.ConnectionAborted);
			}
			catch (Exception exception)
			{
				// The rules can still find a base form; a dictionary outage must not lose the save.
				logger.LogWarning(exception, "Dictionary lookup for the base form of {Term} failed", normalizedTerm);
			}

			VocabularyEntry entry = new()
			{
				NormalizedTerm = normalizedTerm,
				// Null when the dictionary has no entry: the suffix rules then answer at match time.
				BaseForm = WordForms.BaseFromDictionary(normalizedTerm, definition),
				SurfaceForm = surfaceForm.Trim(),
				NovelName = novelName,
				SavedAtUtc = DateTime.UtcNow
			};
			await vocabularyRepository.AddAsync(userName, entry, Context.ConnectionAborted);

			// Underlines are baked into cached markup, so those chapters are now stale (F5).
			await preparedChapterCache.InvalidateForUserAsync(userName, Context.ConnectionAborted);

			await Clients.Caller.SendAsync("ReturnVocabularyChanged", new VocabularyChangedResponse(
				normalizedTerm, true, [.. VocabularyForms.FormsOf(entry)]));
		}

		/// <summary>
		/// Forgets a word. Asked for by any of its forms — the reader may be looking at `running`
		/// with `run` saved — so the form is traced back to the word that was actually kept (D39).
		/// </summary>
		public async Task DeleteWord(string surfaceForm)
		{
			string userName = CallerName;

			string normalizedTerm = TermNormalizer.Normalize(surfaceForm);
			if (normalizedTerm.Length == 0)
			{
				return;
			}

			IReadOnlyCollection<VocabularyEntry> entries =
				await vocabularyRepository.GetAllForUserAsync(userName, Context.ConnectionAborted);
			string savedTerm = new VocabularyForms(entries).SavedTermFor(normalizedTerm) ?? normalizedTerm;
			VocabularyEntry? removed = entries.FirstOrDefault(entry => entry.NormalizedTerm == savedTerm);

			await vocabularyRepository.RemoveAsync(userName, savedTerm, Context.ConnectionAborted);
			await preparedChapterCache.InvalidateForUserAsync(userName, Context.ConnectionAborted);

			await Clients.Caller.SendAsync("ReturnVocabularyChanged", new VocabularyChangedResponse(
				savedTerm, false, removed is null ? [savedTerm] : [.. VocabularyForms.FormsOf(removed)]));
		}

		private async Task<VocabularyForms> VocabularyFormsAsync(string userName) =>
			new(await vocabularyRepository.GetAllForUserAsync(userName, Context.ConnectionAborted));
	}
}
