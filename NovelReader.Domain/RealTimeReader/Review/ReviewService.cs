using System.Collections.Concurrent;
using NovelReader.Domain.RealTimeReader.Assistant;
using NovelReader.Domain.RealTimeReader.Definitions;
using NovelReader.Domain.RealTimeReader.Translation;
using NovelReader.Domain.RealTimeReader.Vocabulary;

namespace NovelReader.Domain.RealTimeReader.Review
{
	/// <summary>A round given up: the answers, and the words graded "again".</summary>
	public sealed record GiveUpOutcome(
		string? Reference,
		IReadOnlyDictionary<string, string> Solution,
		IReadOnlyList<GradedWord> Graded);

	/// <summary>
	/// The vocabulary review. The server decides everything: which words are due (Anki's
	/// schedule, <see cref="ReviewScheduler"/>), in which form they are tested
	/// (<see cref="ReviewPlanner"/>), and whether an answer was right — a match against the key
	/// it keeps, a free answer by asking the reader's AI (<see cref="ReviewTutor"/>). The page
	/// only shows the exercise and sends back what the reader did.
	///
	/// The review needs the reader's AI throughout, so a reader without one is refused.
	///
	/// One exercise is open per reader at a time, held in memory. A restart, or asking for the
	/// next one, simply drops it; nothing is graded until it is answered.
	/// </summary>
	public sealed class ReviewService(
		IReviewCardStore cards,
		IVocabularyRepository vocabulary,
		AssistantSettingsService assistantSettings,
		ITranslationSettingsStore translationSettings,
		DefinitionLookupService dictionary,
		ReviewTutor tutor,
		TimeProvider clock)
	{
		/// <summary>A dictionary sense longer than this is cut, when it stands in for a gloss.</summary>
		private const int MaximumFallbackGlossLength = 140;

		/// <summary>How many words a match needs at least, made up from other words when few are due.</summary>
		private const int MinimumMatchPairs = 3;

		private readonly ConcurrentDictionary<string, OpenExercise> _open = new(StringComparer.Ordinal);

		public async Task<(ReviewSummary? Summary, ReviewFailure Failure)> SummariseAsync(
			string userName,
			CancellationToken cancellationToken = default)
		{
			if (await assistantSettings.GetAsync(userName, cancellationToken) is null)
			{
				return (null, ReviewFailure.AssistantRequired);
			}

			Deck deck = await LoadDeckAsync(userName, cancellationToken);
			return (ReviewPlanner.Summarise(deck.Cards, clock.GetUtcNow().UtcDateTime), ReviewFailure.None);
		}

		/// <summary>The next exercise, or null with no failure when nothing is due.</summary>
		public async Task<(ReviewExercise? Exercise, ReviewFailure Failure)> NextAsync(
			string userName,
			CancellationToken cancellationToken = default)
		{
			AssistantSettings? settings = await assistantSettings.GetAsync(userName, cancellationToken);
			if (settings is null)
			{
				return (null, ReviewFailure.AssistantRequired);
			}

			_open.TryRemove(userName, out _);

			DateTime now = clock.GetUtcNow().UtcDateTime;
			Deck deck = await LoadDeckAsync(userName, cancellationToken);
			IReadOnlyList<ReviewCard> queue = ReviewPlanner.Queue(deck.Cards, deck.SavedAt, now);
			if (queue.Count == 0)
			{
				return (null, ReviewFailure.None);
			}

			TranslationSettings? translation = await translationSettings.GetAsync(userName, cancellationToken);
			TranslationLanguage? language = TranslationLanguages.All.FirstOrDefault(candidate => candidate.Code == translation?.TargetLanguage);

			ReviewCard first = queue[0];
			OpenExercise? exercise = ReviewPlanner.KindFor(first, language is not null) switch
			{
				ExerciseKind.Match => await MatchAsync(userName, settings, deck, queue, cancellationToken),
				ExerciseKind.Translate => await TranslateAsync(settings, deck, first, language!, cancellationToken),
				ExerciseKind.Recall => await PromptAsync(ExerciseKind.Recall, deck, first, cancellationToken),
				_ => await PromptAsync(ExerciseKind.Sentence, deck, first, cancellationToken)
			};

			// A match needs definitions, a translation a sentence; when the AI could give neither,
			// the word is asked about in words instead, which needs nothing from it up front.
			exercise ??= await PromptAsync(ExerciseKind.Recall, deck, first, cancellationToken);

			_open[userName] = exercise;
			return (exercise.ToView(queue.Count), ReviewFailure.None);
		}

		/// <summary>
		/// One attempted pair. A word joined to the wrong definition is marked; once every pair is
		/// joined, each word due for review is graded — "good" when it was joined at the first
		/// try, "again" when it was not.
		/// </summary>
		public async Task<(MatchCheck? Check, ReviewFailure Failure)> CheckMatchAsync(
			string userName,
			string exerciseId,
			string wordId,
			string definitionId,
			CancellationToken cancellationToken = default)
		{
			if (!_open.TryGetValue(userName, out OpenExercise? exercise)
				|| exercise.Id != exerciseId
				|| exercise.Kind != ExerciseKind.Match
				|| !exercise.Pairs.TryGetValue(wordId, out MatchPair? pair))
			{
				return (null, ReviewFailure.ExerciseUnknown);
			}

			bool correct;
			lock (exercise)
			{
				correct = pair.DefinitionId == definitionId;
				if (!correct)
				{
					pair.Mistakes++;
				}
				else
				{
					pair.Matched = true;
				}
			}

			if (!exercise.Pairs.Values.All(candidate => candidate.Matched) || !_open.TryRemove(new(userName, exercise)))
			{
				return (new MatchCheck(correct, false, []), ReviewFailure.None);
			}

			List<GradedWord> graded = [];
			foreach (MatchPair joined in exercise.Pairs.Values.Where(candidate => candidate.Card is not null))
			{
				ReviewRating rating = joined.Mistakes == 0 ? ReviewRating.Good : ReviewRating.Again;
				graded.Add(await GradeAsync(userName, joined.Card!, joined.Word, rating, cancellationToken));
			}

			return (new MatchCheck(correct, true, graded), ReviewFailure.None);
		}

		/// <summary>A free answer — typed or spoken — judged by the reader's AI and graded on its verdict.</summary>
		public async Task<(AnswerOutcome? Outcome, ReviewFailure Failure)> AnswerAsync(
			string userName,
			string exerciseId,
			string? answer,
			CancellationToken cancellationToken = default)
		{
			if (!_open.TryGetValue(userName, out OpenExercise? exercise)
				|| exercise.Id != exerciseId
				|| exercise.Kind == ExerciseKind.Match)
			{
				return (null, ReviewFailure.ExerciseUnknown);
			}

			string trimmed = (answer ?? string.Empty).Trim();
			if (trimmed.Length == 0 || trimmed.Length > ReviewTutor.MaximumAnswerLength)
			{
				return (null, ReviewFailure.AnswerInvalid);
			}

			AssistantSettings? settings = await assistantSettings.GetAsync(userName, cancellationToken);
			if (settings is null)
			{
				return (null, ReviewFailure.AssistantRequired);
			}

			Judgement? judgement = exercise.Kind switch
			{
				ExerciseKind.Recall => await tutor.JudgeRecallAsync(settings, exercise.Word!, exercise.Reference!, trimmed, cancellationToken),
				ExerciseKind.Sentence => await tutor.JudgeSentenceAsync(settings, exercise.Word!, exercise.Reference!, trimmed, cancellationToken),
				_ => await tutor.JudgeTranslationAsync(settings, exercise.Word!, exercise.LanguageName!, exercise.Task!, trimmed, cancellationToken)
			};

			// Nothing is graded on a judgement that never came; the exercise stays open to retry.
			if (judgement is null || !_open.TryRemove(new(userName, exercise)))
			{
				return (null, judgement is null ? ReviewFailure.AssistantUnavailable : ReviewFailure.ExerciseUnknown);
			}

			ReviewRating rating = judgement.Verdict switch
			{
				AnswerVerdict.Correct => ReviewRating.Good,
				AnswerVerdict.Partial => ReviewRating.Hard,
				_ => ReviewRating.Again
			};

			GradedWord graded = await GradeAsync(userName, exercise.Card!, exercise.Word!, rating, cancellationToken);
			string reference = exercise.Kind == ExerciseKind.Translate ? exercise.Task!.English : exercise.Display!;
			return (new AnswerOutcome(judgement.Verdict, judgement.Feedback, reference, graded), ReviewFailure.None);
		}

		/// <summary>
		/// "I don't know": the answer is shown and the word goes back to the start. In a match,
		/// every word not yet joined cleanly is graded "again" and the whole key is shown.
		/// </summary>
		public async Task<(GiveUpOutcome? Outcome, ReviewFailure Failure)> GiveUpAsync(
			string userName,
			string exerciseId,
			CancellationToken cancellationToken = default)
		{
			if (!_open.TryGetValue(userName, out OpenExercise? exercise)
				|| exercise.Id != exerciseId
				|| !_open.TryRemove(new(userName, exercise)))
			{
				return (null, ReviewFailure.ExerciseUnknown);
			}

			List<GradedWord> graded = [];
			if (exercise.Kind == ExerciseKind.Match)
			{
				foreach (MatchPair pair in exercise.Pairs.Values.Where(candidate => candidate.Card is not null))
				{
					ReviewRating rating = pair.Matched && pair.Mistakes == 0 ? ReviewRating.Good : ReviewRating.Again;
					graded.Add(await GradeAsync(userName, pair.Card!, pair.Word, rating, cancellationToken));
				}

				return (new GiveUpOutcome(
					null,
					exercise.Pairs.ToDictionary(entry => entry.Key, entry => entry.Value.DefinitionId),
					graded), ReviewFailure.None);
			}

			graded.Add(await GradeAsync(userName, exercise.Card!, exercise.Word!, ReviewRating.Again, cancellationToken));
			string reference = exercise.Kind == ExerciseKind.Translate ? exercise.Task!.English : exercise.Display!;
			return (new GiveUpOutcome(reference, new Dictionary<string, string>(), graded), ReviewFailure.None);
		}

		private async Task<GradedWord> GradeAsync(
			string userName,
			ReviewCard card,
			string word,
			ReviewRating rating,
			CancellationToken cancellationToken)
		{
			ReviewCard next = ReviewScheduler.Rate(card, rating, clock.GetUtcNow().UtcDateTime);
			await cards.SaveAsync(userName, next, cancellationToken);
			return new GradedWord(word, rating, next.DueUtc);
		}

		private async Task<OpenExercise?> MatchAsync(
			string userName,
			AssistantSettings settings,
			Deck deck,
			IReadOnlyList<ReviewCard> queue,
			CancellationToken cancellationToken)
		{
			List<ReviewCard> due = [.. queue
				.Where(card => ReviewPlanner.KindFor(card, canTranslate: false) == ExerciseKind.Match)
				.Take(ReviewPlanner.MatchSize)];

			// Definitions are written once per word and kept on its card.
			List<ReviewCard> unglossed = [.. due.Where(card => card.Gloss is null)];
			if (unglossed.Count > 0)
			{
				IReadOnlyDictionary<string, string> glosses = await tutor.GlossesAsync(
					settings, [.. unglossed.Select(deck.Headword)], cancellationToken);

				for (int index = 0; index < due.Count; index++)
				{
					ReviewCard card = due[index];
					if (card.Gloss is null && glosses.TryGetValue(deck.Headword(card), out string? gloss))
					{
						due[index] = card with { Gloss = gloss };
						await cards.SaveAsync(userName, due[index], cancellationToken);
					}
				}
			}

			List<(ReviewCard Card, string Gloss)> graded = [];
			foreach (ReviewCard card in due)
			{
				if ((card.Gloss ?? await DictionaryGlossAsync(deck.Headword(card), cancellationToken)) is { } gloss)
				{
					graded.Add((card, gloss));
				}
			}

			if (graded.Count == 0)
			{
				return null;
			}

			// Few words due: others the reader knows make up the numbers, ungraded.
			HashSet<string> taken = [.. graded.Select(entry => entry.Card.Term)];
			List<(ReviewCard Card, string Gloss)> fillers = [.. deck.Cards
				.Where(card => card.Gloss is not null && !taken.Contains(card.Term))
				.OrderBy(_ => Random.Shared.Next())
				.Take(Math.Max(0, MinimumMatchPairs - graded.Count))
				.Select(card => (card, card.Gloss!))];

			List<(ReviewCard Card, string Gloss, bool Graded)> all = [
				.. graded.Select(entry => (entry.Card, entry.Gloss, true)),
				.. fillers.Select(entry => (entry.Card, entry.Gloss, false))];

			int[] order = [.. Enumerable.Range(0, all.Count).OrderBy(_ => Random.Shared.Next())];
			OpenExercise exercise = new(ExerciseKind.Match);
			for (int index = 0; index < all.Count; index++)
			{
				(ReviewCard card, string gloss, bool isGraded) = all[index];
				string definitionId = $"d{Array.IndexOf(order, index)}";
				exercise.Pairs[$"w{index}"] = new MatchPair(deck.Headword(card), gloss, definitionId, isGraded ? card : null);
			}

			return exercise;
		}

		private async Task<OpenExercise?> TranslateAsync(
			AssistantSettings settings,
			Deck deck,
			ReviewCard card,
			TranslationLanguage language,
			CancellationToken cancellationToken)
		{
			string word = deck.Headword(card);
			(string reference, _) = await ReferenceAsync(card, word, cancellationToken);
			TranslationTask? task = await tutor.TranslationTaskAsync(settings, word, reference, language.Name, cancellationToken);
			if (task is null)
			{
				return null;
			}

			return new OpenExercise(ExerciseKind.Translate)
			{
				Card = card,
				Word = word,
				Reference = reference,
				Task = task,
				LanguageName = language.Name
			};
		}

		private async Task<OpenExercise> PromptAsync(
			ExerciseKind kind,
			Deck deck,
			ReviewCard card,
			CancellationToken cancellationToken)
		{
			string word = deck.Headword(card);
			(string reference, string display) = await ReferenceAsync(card, word, cancellationToken);
			return new OpenExercise(kind) { Card = card, Word = word, Reference = reference, Display = display };
		}

		/// <summary>
		/// What the AI judges an answer against — the gloss and the dictionary's first senses —
		/// and the shorter version the reader is shown afterwards.
		/// </summary>
		private async Task<(string Reference, string Display)> ReferenceAsync(
			ReviewCard card,
			string word,
			CancellationToken cancellationToken)
		{
			WordDefinition? definition = await LookUpQuietlyAsync(word, cancellationToken);
			List<string> parts = [];
			if (card.Gloss is not null)
			{
				parts.Add(card.Gloss);
			}

			parts.AddRange((definition?.Senses ?? []).Take(2).Select(sense => sense.Text));
			string display = card.Gloss ?? definition?.Senses.FirstOrDefault()?.Text ?? "(no definition on record)";
			return (parts.Count == 0 ? "(no reference — judge from your own knowledge)" : string.Join(" | ", parts), display);
		}

		private async Task<string?> DictionaryGlossAsync(string word, CancellationToken cancellationToken)
		{
			string? text = (await LookUpQuietlyAsync(word, cancellationToken))?.Senses.FirstOrDefault()?.Text;
			return text is null || text.Length <= MaximumFallbackGlossLength
				? text
				: text[..(MaximumFallbackGlossLength - 1)].TrimEnd() + "…";
		}

		private async Task<WordDefinition?> LookUpQuietlyAsync(string word, CancellationToken cancellationToken)
		{
			try
			{
				return await dictionary.LookUpAsync(word, cancellationToken);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				// A dictionary outage costs the review a reference, not the exercise.
				return null;
			}
		}

		/// <summary>
		/// The reader's cards, one for every saved word: a word saved since the last review gets a
		/// new card (stored once it is first graded), and the card of a deleted word goes.
		/// </summary>
		private async Task<Deck> LoadDeckAsync(string userName, CancellationToken cancellationToken)
		{
			IReadOnlyCollection<VocabularyEntry> entries = await vocabulary.GetAllForUserAsync(userName, cancellationToken);
			IReadOnlyList<ReviewCard> stored = await cards.GetAllAsync(userName, cancellationToken);

			Dictionary<string, VocabularyEntry> byTerm = entries
				.Where(entry => entry.NormalizedTerm.Length > 0)
				.GroupBy(entry => entry.NormalizedTerm)
				.ToDictionary(group => group.Key, group => group.First());

			List<ReviewCard> deck = [];
			foreach (ReviewCard card in stored)
			{
				if (byTerm.ContainsKey(card.Term))
				{
					deck.Add(card);
				}
				else
				{
					await cards.RemoveAsync(userName, card.Term, cancellationToken);
				}
			}

			HashSet<string> carded = [.. deck.Select(card => card.Term)];
			foreach (VocabularyEntry entry in byTerm.Values.Where(entry => !carded.Contains(entry.NormalizedTerm)))
			{
				deck.Add(new ReviewCard { Term = entry.NormalizedTerm, DueUtc = entry.SavedAtUtc });
			}

			return new Deck(deck, byTerm);
		}

		private sealed class Deck(IReadOnlyList<ReviewCard> cards, IReadOnlyDictionary<string, VocabularyEntry> entries)
		{
			public IReadOnlyList<ReviewCard> Cards { get; } = cards;

			public IReadOnlyDictionary<string, DateTime> SavedAt { get; } =
				entries.ToDictionary(entry => entry.Key, entry => entry.Value.SavedAtUtc);

			/// <summary>
			/// The word as it is asked about: its base form when the dictionary gave one (D39) — the
			/// reader saved `sneered`, the definition is of `sneer` — or the word as saved.
			/// </summary>
			public string Headword(ReviewCard card) =>
				entries.TryGetValue(card.Term, out VocabularyEntry? entry) && !string.IsNullOrWhiteSpace(entry.BaseForm)
					? entry.BaseForm
					: card.Term;
		}

		private sealed class MatchPair(string word, string gloss, string definitionId, ReviewCard? card)
		{
			public string Word { get; } = word;
			public string Gloss { get; } = gloss;
			public string DefinitionId { get; } = definitionId;
			/// <summary>Null for a word brought in only to make up the numbers, which is not graded.</summary>
			public ReviewCard? Card { get; } = card;
			public int Mistakes { get; set; }
			public bool Matched { get; set; }
		}

		private sealed class OpenExercise(ExerciseKind kind)
		{
			public string Id { get; } = Guid.NewGuid().ToString("N");
			public ExerciseKind Kind { get; } = kind;

			/// <summary>A match's pairs, keyed by word id.</summary>
			public Dictionary<string, MatchPair> Pairs { get; } = [];

			public ReviewCard? Card { get; init; }
			public string? Word { get; init; }
			public string? Reference { get; init; }
			public string? Display { get; init; }
			public TranslationTask? Task { get; init; }
			public string? LanguageName { get; init; }

			public ReviewExercise ToView(int remaining) => new(
				Id,
				Kind,
				[.. Pairs.Select(pair => new MatchItem(pair.Key, pair.Value.Word))],
				[.. Pairs.Values.OrderBy(pair => pair.DefinitionId, StringComparer.Ordinal).Select(pair => new MatchItem(pair.DefinitionId, pair.Gloss))],
				Kind == ExerciseKind.Translate ? Task!.Sentence : Word,
				LanguageName,
				remaining);
		}
	}
}
