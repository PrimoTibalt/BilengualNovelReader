namespace NovelReader.Domain.RealTimeReader.Review
{
	/// <summary>Where a card is in Anki's life cycle.</summary>
	public enum CardState
	{
		/// <summary>Saved, never reviewed.</summary>
		New,
		/// <summary>Being learnt: comes back after minutes, through <see cref="ReviewScheduler.LearningSteps"/>.</summary>
		Learning,
		/// <summary>Graduated: comes back after days.</summary>
		Review,
		/// <summary>Forgotten after graduating: back through the short relearning steps.</summary>
		Relearning
	}

	/// <summary>How well the reader knew a word, as Anki's four buttons put it.</summary>
	public enum ReviewRating
	{
		Again,
		Hard,
		Good,
		Easy
	}

	/// <summary>
	/// One saved word's review schedule — one per (reader, saved term). The card follows the
	/// vocabulary: it is made the first time the reader reviews after saving the word, and goes
	/// when the word is deleted.
	/// </summary>
	public sealed record ReviewCard
	{
		/// <summary>The saved term, as <see cref="Vocabulary.VocabularyEntry.NormalizedTerm"/>.</summary>
		public required string Term { get; init; }

		public CardState State { get; init; } = CardState.New;

		/// <summary>Which learning or relearning step the card is on.</summary>
		public int Step { get; init; }

		/// <summary>Anki's ease: how much the interval grows on a "good". Starts at 2.5.</summary>
		public double Ease { get; init; } = ReviewScheduler.StartingEase;

		/// <summary>The current interval in days; 0 until the card graduates.</summary>
		public double IntervalDays { get; init; }

		public required DateTime DueUtc { get; init; }

		public int Reviews { get; init; }

		public int Lapses { get; init; }

		/// <summary>When the card was first reviewed, which is what the daily new-card limit counts.</summary>
		public DateTime? FirstReviewedUtc { get; init; }

		public DateTime? LastReviewedUtc { get; init; }

		/// <summary>
		/// A one-line definition the reader's AI wrote for matching it, kept so it is asked for
		/// once per word rather than once per review. Null until the first match needs one.
		/// </summary>
		public string? Gloss { get; init; }
	}
}
