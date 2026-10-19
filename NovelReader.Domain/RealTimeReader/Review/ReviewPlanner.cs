namespace NovelReader.Domain.RealTimeReader.Review
{
	/// <summary>
	/// Decides what is reviewed next and in which form — the server's half of the review, kept
	/// free of storage and of the AI so the rules can be read and tested on their own.
	/// </summary>
	public static class ReviewPlanner
	{
		/// <summary>How many words one match round joins up.</summary>
		public const int MatchSize = 5;

		/// <summary>
		/// A learning card due within this long is shown now when nothing else is due — Anki's
		/// "learn ahead limit" — so a session does not stall on a one-minute step.
		/// </summary>
		public static readonly TimeSpan LearnAhead = TimeSpan.FromMinutes(20);

		/// <summary>Below this interval a word is still matched; the reader has met it only a few times.</summary>
		public const double RecallFromDays = 3;

		/// <summary>From this interval on, the word is used in a sentence or a translation.</summary>
		public const double ProduceFromDays = 10;

		/// <summary>
		/// The form a card is reviewed in. Matching first, while the word is new; then saying what
		/// it means; then using it — a sentence of the reader's own, or, for a reader who has a
		/// language set, a sentence of theirs to put into English, the two taking turns.
		/// </summary>
		public static ExerciseKind KindFor(ReviewCard card, bool canTranslate)
		{
			if (card.State != CardState.Review || card.IntervalDays < RecallFromDays)
			{
				return ExerciseKind.Match;
			}

			if (card.IntervalDays < ProduceFromDays)
			{
				return ExerciseKind.Recall;
			}

			return canTranslate && card.Reviews % 2 == 0 ? ExerciseKind.Translate : ExerciseKind.Sentence;
		}

		/// <summary>
		/// The cards to review, in order: learning cards that are due, then graduated cards that
		/// are due, most overdue first, then as many new cards as today's allowance leaves, in
		/// the order the words were saved. When none of that is due, a learning card due shortly
		/// is taken early.
		/// </summary>
		public static IReadOnlyList<ReviewCard> Queue(
			IReadOnlyCollection<ReviewCard> cards,
			IReadOnlyDictionary<string, DateTime> savedAtByTerm,
			DateTime nowUtc)
		{
			List<ReviewCard> learning = [.. cards
				.Where(card => card.State is CardState.Learning or CardState.Relearning && card.DueUtc <= nowUtc)
				.OrderBy(card => card.DueUtc)];
			List<ReviewCard> review = [.. cards
				.Where(card => card.State == CardState.Review && card.DueUtc <= nowUtc)
				.OrderBy(card => card.DueUtc)];
			List<ReviewCard> fresh = [.. cards
				.Where(card => card.State == CardState.New)
				.OrderBy(card => savedAtByTerm.GetValueOrDefault(card.Term))
				.Take(NewAllowance(cards, nowUtc))];

			List<ReviewCard> queue = [.. learning, .. review, .. fresh];
			if (queue.Count > 0)
			{
				return queue;
			}

			return [.. cards
				.Where(card => card.State is CardState.Learning or CardState.Relearning && card.DueUtc <= nowUtc + LearnAhead)
				.OrderBy(card => card.DueUtc)];
		}

		/// <summary>New words still to be introduced today (days counted in UTC).</summary>
		public static int NewAllowance(IReadOnlyCollection<ReviewCard> cards, DateTime nowUtc)
		{
			int introducedToday = cards.Count(card => card.FirstReviewedUtc is { } first && first.Date == nowUtc.Date);
			return Math.Max(0, ReviewScheduler.NewCardsPerDay - introducedToday);
		}

		public static ReviewSummary Summarise(IReadOnlyCollection<ReviewCard> cards, DateTime nowUtc)
		{
			int due = cards.Count(card => card.State != CardState.New && card.DueUtc <= nowUtc);
			int fresh = Math.Min(cards.Count(card => card.State == CardState.New), NewAllowance(cards, nowUtc));
			DateTime? next = cards
				.Where(card => card.State != CardState.New && card.DueUtc > nowUtc)
				.Select(card => (DateTime?)card.DueUtc)
				.Min();

			return new ReviewSummary(due, fresh, cards.Count, next);
		}
	}
}
