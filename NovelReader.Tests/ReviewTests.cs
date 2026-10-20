using NovelReader.Domain.RealTimeReader.Assistant;
using NovelReader.Domain.RealTimeReader.Definitions;
using NovelReader.Domain.RealTimeReader.Review;
using NovelReader.Domain.RealTimeReader.Translation;
using NovelReader.Domain.RealTimeReader.Vocabulary;

namespace NovelReader.Tests
{
	/// <summary>Anki's schedule, as <see cref="ReviewScheduler"/> applies it.</summary>
	public class ReviewSchedulerTests
	{
		private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

		private static ReviewCard NewCard() => new() { Term = "sneer", DueUtc = Now };

		[Fact]
		public void A_new_word_steps_through_a_minute_and_ten_minutes_then_graduates_to_a_day()
		{
			ReviewCard first = ReviewScheduler.Rate(NewCard(), ReviewRating.Good, Now);
			Assert.Equal((CardState.Learning, 1), (first.State, first.Step));
			Assert.Equal(Now.AddMinutes(10), first.DueUtc);

			ReviewCard graduated = ReviewScheduler.Rate(first, ReviewRating.Good, Now);
			Assert.Equal(CardState.Review, graduated.State);
			Assert.Equal(1, graduated.IntervalDays);
			Assert.Equal(Now.AddDays(1), graduated.DueUtc);
		}

		[Fact]
		public void Again_while_learning_starts_the_steps_over_and_hard_waits_halfway()
		{
			ReviewCard again = ReviewScheduler.Rate(NewCard() with { State = CardState.Learning, Step = 1 }, ReviewRating.Again, Now);
			Assert.Equal((0, Now.AddMinutes(1)), (again.Step, again.DueUtc));

			ReviewCard hard = ReviewScheduler.Rate(NewCard(), ReviewRating.Hard, Now);
			Assert.Equal(Now.AddMinutes(5.5), hard.DueUtc);
		}

		[Fact]
		public void Easy_on_a_new_word_graduates_it_straight_to_four_days()
		{
			ReviewCard easy = ReviewScheduler.Rate(NewCard(), ReviewRating.Easy, Now);
			Assert.Equal((CardState.Review, 4.0), (easy.State, easy.IntervalDays));
		}

		[Fact]
		public void A_graduated_word_grows_by_its_ease_and_hard_and_easy_move_the_ease()
		{
			ReviewCard card = NewCard() with { State = CardState.Review, IntervalDays = 10, DueUtc = Now };

			Assert.Equal(25, ReviewScheduler.Rate(card, ReviewRating.Good, Now).IntervalDays);

			ReviewCard hard = ReviewScheduler.Rate(card, ReviewRating.Hard, Now);
			Assert.Equal((12.0, 2.35), (hard.IntervalDays, Math.Round(hard.Ease, 2)));

			ReviewCard easy = ReviewScheduler.Rate(card, ReviewRating.Easy, Now);
			Assert.Equal((33.0, 2.65), (easy.IntervalDays, Math.Round(easy.Ease, 2)));
		}

		[Fact]
		public void Forgetting_a_graduated_word_relearns_it_and_costs_ease_down_to_the_floor()
		{
			ReviewCard card = NewCard() with { State = CardState.Review, IntervalDays = 30, Ease = 1.4, DueUtc = Now };

			ReviewCard lapsed = ReviewScheduler.Rate(card, ReviewRating.Again, Now);
			Assert.Equal(CardState.Relearning, lapsed.State);
			Assert.Equal((1, ReviewScheduler.MinimumEase), (lapsed.Lapses, lapsed.Ease));
			Assert.Equal(Now.AddMinutes(10), lapsed.DueUtc);

			ReviewCard back = ReviewScheduler.Rate(lapsed, ReviewRating.Good, Now);
			Assert.Equal((CardState.Review, 1.0), (back.State, back.IntervalDays));
		}

		[Fact]
		public void A_late_review_gets_credit_for_the_extra_days()
		{
			ReviewCard card = NewCard() with { State = CardState.Review, IntervalDays = 10, DueUtc = Now.AddDays(-4) };
			Assert.Equal(30, ReviewScheduler.Rate(card, ReviewRating.Good, Now).IntervalDays);
		}
	}

	/// <summary>What is reviewed next, and in which form.</summary>
	public class ReviewPlannerTests
	{
		private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

		[Fact]
		public void Words_are_matched_first_then_recalled_then_used()
		{
			ReviewCard card = new() { Term = "sneer", DueUtc = Now };
			Assert.Equal(ExerciseKind.Match, ReviewPlanner.KindFor(card, canTranslate: true));
			Assert.Equal(ExerciseKind.Match, ReviewPlanner.KindFor(card with { State = CardState.Review, IntervalDays = 1 }, true));
			Assert.Equal(ExerciseKind.Recall, ReviewPlanner.KindFor(card with { State = CardState.Review, IntervalDays = 3 }, true));

			ReviewCard mature = card with { State = CardState.Review, IntervalDays = 20 };
			Assert.Equal(ExerciseKind.Translate, ReviewPlanner.KindFor(mature with { Reviews = 6 }, canTranslate: true));
			Assert.Equal(ExerciseKind.Sentence, ReviewPlanner.KindFor(mature with { Reviews = 7 }, canTranslate: true));
			Assert.Equal(ExerciseKind.Sentence, ReviewPlanner.KindFor(mature with { Reviews = 6 }, canTranslate: false));
		}

		[Fact]
		public void Learning_comes_first_then_overdue_reviews_then_new_words_in_the_order_saved()
		{
			ReviewCard[] cards =
			[
				new() { Term = "new-late", DueUtc = Now },
				new() { Term = "review", State = CardState.Review, IntervalDays = 5, DueUtc = Now.AddHours(-1) },
				new() { Term = "new-early", DueUtc = Now },
				new() { Term = "learning", State = CardState.Learning, DueUtc = Now.AddMinutes(-1) },
				new() { Term = "not-due", State = CardState.Review, IntervalDays = 5, DueUtc = Now.AddDays(1) },
			];
			Dictionary<string, DateTime> savedAt = new() { ["new-late"] = Now.AddDays(-1), ["new-early"] = Now.AddDays(-2) };

			Assert.Equal(
				["learning", "review", "new-early", "new-late"],
				ReviewPlanner.Queue(cards, savedAt, Now).Select(card => card.Term));
		}

		[Fact]
		public void Twenty_new_words_a_day_and_a_learning_word_due_soon_is_taken_early()
		{
			List<ReviewCard> cards = [.. Enumerable.Range(0, 20).Select(index => new ReviewCard
			{
				Term = $"met{index}", State = CardState.Review, IntervalDays = 1, DueUtc = Now.AddDays(1), FirstReviewedUtc = Now.AddHours(-1)
			})];
			cards.Add(new ReviewCard { Term = "unmet", DueUtc = Now });
			cards.Add(new ReviewCard { Term = "soon", State = CardState.Learning, DueUtc = Now.AddMinutes(5) });

			Assert.Equal(["soon"], ReviewPlanner.Queue(cards, new Dictionary<string, DateTime>(), Now).Select(card => card.Term));
			Assert.Equal(0, ReviewPlanner.Summarise(cards, Now).New);
		}
	}

	/// <summary>Reading the AI's answers, which arrive wrapped in whatever the model felt like.</summary>
	public class ReviewTutorTests
	{
		[Theory]
		[InlineData("{\"verdict\":\"correct\",\"feedback\":\"Yes.\"}", AnswerVerdict.Correct)]
		[InlineData("```json\n{\"verdict\": \"Partial\", \"feedback\": \"Close.\"}\n```", AnswerVerdict.Partial)]
		[InlineData("Here you go: {\"verdict\":\"wrong\",\"feedback\":\"No.\"} hope it helps", AnswerVerdict.Wrong)]
		public void A_judgement_is_read_out_of_the_reply(string reply, AnswerVerdict verdict)
		{
			Assert.Equal(verdict, ReviewTutor.ReadJudgement(reply)?.Verdict);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("I think they are right.")]
		[InlineData("{\"verdict\":\"maybe\"}")]
		public void Anything_else_is_no_judgement(string? reply)
		{
			Assert.Null(ReviewTutor.ReadJudgement(reply));
		}

		private static readonly AssistantSettings Settings = new("digitalocean", "glm-5.3", "sk-do-1234567890");

		[Fact]
		public async Task The_quick_judge_answers_first_and_the_ai_is_not_asked()
		{
			ScriptedAssistant assistant = new();
			ReviewTutor tutor = new(assistant, new FixedJudge(AnswerVerdict.Partial));

			Judgement? judgement = await tutor.JudgeRecallAsync(Settings, "sneer", "a contemptuous smile", "a smile");

			Assert.Equal(new Judgement(AnswerVerdict.Partial, string.Empty), judgement);
			Assert.Equal(0, assistant.Calls);
		}

		[Fact]
		public async Task When_the_quick_judge_has_no_verdict_the_ai_judges_and_explains()
		{
			ScriptedAssistant assistant = new();
			assistant.Answers["what a word means"] = _ => "{\"verdict\":\"correct\",\"feedback\":\"Yes.\"}";
			ReviewTutor tutor = new(assistant, new FixedJudge(null));

			Judgement? judgement = await tutor.JudgeRecallAsync(Settings, "sneer", "a contemptuous smile", "a mocking smile");

			Assert.Equal(new Judgement(AnswerVerdict.Correct, "Yes."), judgement);
			Assert.Equal(1, assistant.Calls);
		}

		private sealed class FixedJudge(AnswerVerdict? verdict) : IAnswerJudge
		{
			public Task<AnswerVerdict?> JudgeAsync(
				string instructions,
				IReadOnlyDictionary<AnswerVerdict, string> verdicts,
				string answer,
				CancellationToken cancellationToken = default) => Task.FromResult(verdict);
		}
	}

	/// <summary>A review session end to end, against fakes for the stores and the AI.</summary>
	public class ReviewServiceTests
	{
		private readonly InMemoryAssistantSettingsStore assistantStore = new();
		private readonly InMemoryTranslationSettingsStore translationStore = new();
		private readonly ScriptedAssistant assistant = new();
		private readonly InMemoryReviewCardStore cardStore = new();
		private readonly ListVocabularyRepository vocabulary = new();
		private readonly ManualTimeProvider clock = new();
		private readonly ReviewService service;

		public ReviewServiceTests()
		{
			AssistantSettingsService settings = new(assistantStore, new ReversingProtector());
			service = new ReviewService(
				cardStore,
				vocabulary,
				settings,
				translationStore,
				new DefinitionLookupService(new OneSenseProvider(), new NoCache()),
				new ReviewTutor(assistant),
				clock);

			assistant.Answers["dictionary definitions"] = question => "{\"glosses\":{"
				+ string.Join(',', question.Split('\n').Select(word => $"\"{word}\":\"meaning of {word}\"")) + "}}";
		}

		private async Task WithAssistantAsync() =>
			await new AssistantSettingsService(assistantStore, new ReversingProtector())
				.SaveAsync("anton", "digitalocean", "glm-5.3", "sk-do-1234567890");

		private void Save(params string[] terms)
		{
			foreach (string term in terms)
			{
				vocabulary.Entries.Add(new VocabularyEntry
				{
					NormalizedTerm = term, SurfaceForm = term, NovelName = "reverend-insanity", SavedAtUtc = clock.GetUtcNow().UtcDateTime
				});
			}
		}

		[Fact]
		public async Task Without_an_ai_the_review_is_refused()
		{
			Save("sneer");
			(ReviewExercise? exercise, ReviewFailure failure) = await service.NextAsync("anton");

			Assert.Null(exercise);
			Assert.Equal(ReviewFailure.AssistantRequired, failure);
		}

		[Fact]
		public async Task New_words_are_matched_with_glosses_the_ai_wrote_once_and_graded_by_first_try()
		{
			await WithAssistantAsync();
			Save("sneer", "lantern", "gale");

			(ReviewExercise? exercise, _) = await service.NextAsync("anton");
			Assert.NotNull(exercise);
			Assert.Equal(ExerciseKind.Match, exercise.Kind);
			Assert.Equal(3, exercise.Words.Count);
			Assert.Contains(exercise.Definitions, definition => definition.Text == "meaning of gale");

			// Join "sneer" wrongly once, the rest at the first try.
			Dictionary<string, string> key = exercise.Words.ToDictionary(
				word => word.Id,
				word => exercise.Definitions.Single(definition => definition.Text == $"meaning of {word.Text}").Id);
			string sneer = exercise.Words.Single(word => word.Text == "sneer").Id;
			string wrong = key.First(pair => pair.Key != sneer).Value;

			Assert.False((await service.CheckMatchAsync("anton", exercise.Id, sneer, wrong)).Check!.Correct);

			MatchCheck? last = null;
			foreach ((string word, string definition) in key)
			{
				last = (await service.CheckMatchAsync("anton", exercise.Id, word, definition)).Check;
			}

			Assert.True(last!.Completed);
			Assert.Equal(ReviewRating.Again, last.Graded.Single(graded => graded.Word == "sneer").Rating);
			Assert.Equal(ReviewRating.Good, last.Graded.Single(graded => graded.Word == "gale").Rating);
			Assert.All(cardStore.Cards.Values, card => Assert.StartsWith("meaning of", card.Gloss));

			// The glosses are on the cards now: the next round asks the AI for none.
			int asked = assistant.Asked.Count;
			clock.Advance(TimeSpan.FromMinutes(11));
			await service.NextAsync("anton");
			Assert.Equal(asked, assistant.Asked.Count);
		}

		[Fact]
		public async Task A_known_word_is_recalled_in_the_readers_words_and_the_ai_verdict_grades_it()
		{
			await WithAssistantAsync();
			Save("sneer");
			cardStore.Cards["sneer"] = new ReviewCard
			{
				Term = "sneer", State = CardState.Review, IntervalDays = 4, DueUtc = clock.GetUtcNow().UtcDateTime, Gloss = "smile with contempt"
			};
			assistant.Answers["what a word means"] = _ => "{\"verdict\":\"partial\",\"feedback\":\"Nearly: it is scornful.\"}";

			(ReviewExercise? exercise, _) = await service.NextAsync("anton");
			Assert.Equal((ExerciseKind.Recall, "sneer"), (exercise!.Kind, exercise.Prompt));

			(AnswerOutcome? outcome, _) = await service.AnswerAsync("anton", exercise.Id, "to smile");
			Assert.Equal(AnswerVerdict.Partial, outcome!.Verdict);
			Assert.Equal(ReviewRating.Hard, outcome.Graded.Rating);
			Assert.Equal("smile with contempt", outcome.Reference);
			Assert.Contains("smile with contempt", assistant.Asked.Last().Question);
			Assert.Equal(5, cardStore.Cards["sneer"].IntervalDays);
		}

		[Fact]
		public async Task An_ai_that_does_not_answer_grades_nothing_and_the_exercise_stays_open()
		{
			await WithAssistantAsync();
			Save("sneer");
			ReviewCard card = new() { Term = "sneer", State = CardState.Review, IntervalDays = 4, DueUtc = clock.GetUtcNow().UtcDateTime, Gloss = "g" };
			cardStore.Cards["sneer"] = card;

			(ReviewExercise? exercise, _) = await service.NextAsync("anton");
			(AnswerOutcome? outcome, ReviewFailure failure) = await service.AnswerAsync("anton", exercise!.Id, "to smile");

			Assert.Null(outcome);
			Assert.Equal(ReviewFailure.AssistantUnavailable, failure);
			Assert.Same(card, cardStore.Cards["sneer"]);

			assistant.Answers["what a word means"] = _ => "{\"verdict\":\"correct\",\"feedback\":\"Yes.\"}";
			Assert.Equal(ReviewFailure.None, (await service.AnswerAsync("anton", exercise.Id, "to smile")).Failure);
		}

		[Fact]
		public async Task A_mature_word_is_translated_from_the_readers_language()
		{
			await WithAssistantAsync();
			translationStore.Settings = new TranslationSettings("anton@example.com", "ru");
			Save("sneer");
			cardStore.Cards["sneer"] = new ReviewCard
			{
				Term = "sneer", State = CardState.Review, IntervalDays = 20, Reviews = 4, DueUtc = clock.GetUtcNow().UtcDateTime, Gloss = "g"
			};
			assistant.Answers["set translation exercises"] = _ => "{\"sentence\":\"Он презрительно усмехнулся.\",\"english\":\"He sneered.\"}";

			(ReviewExercise? exercise, _) = await service.NextAsync("anton");

			Assert.Equal(ExerciseKind.Translate, exercise!.Kind);
			Assert.Equal(("Он презрительно усмехнулся.", "Russian"), (exercise.Prompt, exercise.LanguageName));

			(GiveUpOutcome? given, _) = await service.GiveUpAsync("anton", exercise.Id);
			Assert.Equal("He sneered.", given!.Reference);
			Assert.Equal(CardState.Relearning, cardStore.Cards["sneer"].State);
		}

		[Fact]
		public async Task A_deleted_word_takes_its_card_with_it()
		{
			await WithAssistantAsync();
			cardStore.Cards["gone"] = new ReviewCard { Term = "gone", DueUtc = clock.GetUtcNow().UtcDateTime };

			(ReviewSummary? summary, _) = await service.SummariseAsync("anton");

			Assert.Equal(0, summary!.Total);
			Assert.Empty(cardStore.Cards);
		}

		private sealed class InMemoryReviewCardStore : IReviewCardStore
		{
			public Dictionary<string, ReviewCard> Cards { get; } = [];

			public Task<IReadOnlyList<ReviewCard>> GetAllAsync(string userName, CancellationToken cancellationToken = default) =>
				Task.FromResult<IReadOnlyList<ReviewCard>>([.. Cards.Values]);

			public Task SaveAsync(string userName, ReviewCard card, CancellationToken cancellationToken = default)
			{
				Cards[card.Term] = card;
				return Task.CompletedTask;
			}

			public Task RemoveAsync(string userName, string term, CancellationToken cancellationToken = default)
			{
				Cards.Remove(term);
				return Task.CompletedTask;
			}
		}

		private sealed class ListVocabularyRepository : IVocabularyRepository
		{
			public List<VocabularyEntry> Entries { get; } = [];

			public Task AddAsync(string userName, VocabularyEntry entry, CancellationToken cancellationToken = default)
			{
				Entries.Add(entry);
				return Task.CompletedTask;
			}

			public Task RemoveAsync(string userName, string normalizedTerm, CancellationToken cancellationToken = default)
			{
				Entries.RemoveAll(entry => entry.NormalizedTerm == normalizedTerm);
				return Task.CompletedTask;
			}

			public Task<bool> ContainsAsync(string userName, string normalizedTerm, CancellationToken cancellationToken = default) =>
				Task.FromResult(Entries.Any(entry => entry.NormalizedTerm == normalizedTerm));

			public Task<IReadOnlyCollection<VocabularyEntry>> GetAllForUserAsync(string userName, CancellationToken cancellationToken = default) =>
				Task.FromResult<IReadOnlyCollection<VocabularyEntry>>([.. Entries]);
		}

		private sealed class OneSenseProvider : IDefinitionProvider
		{
			public string Name => "test";

			public Task<WordDefinition?> LookUpAsync(string term, CancellationToken cancellationToken = default) =>
				Task.FromResult<WordDefinition?>(new WordDefinition
				{
					Term = term,
					Senses = [new DefinitionSense { Text = $"dictionary sense of {term}" }],
					SourceName = Name
				});
		}

		private sealed class NoCache : IDefinitionCache
		{
			public Task<DefinitionLookupResult> TryGetAsync(string normalizedTerm, CancellationToken cancellationToken = default) =>
				Task.FromResult(DefinitionLookupResult.Unknown);

			public Task StoreAsync(WordDefinition definition, CancellationToken cancellationToken = default) => Task.CompletedTask;

			public Task StoreMissAsync(string normalizedTerm, CancellationToken cancellationToken = default) => Task.CompletedTask;
		}
	}
}
