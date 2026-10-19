namespace NovelReader.Domain.RealTimeReader.Review
{
	/// <summary>
	/// Anki's scheduler (its SM-2 variant, with Anki's default deck options), as a pure function
	/// from a card, a rating and the time to the card's next state.
	///
	/// A new word goes through short learning steps — a minute, then ten — and graduates to a
	/// day. From there each "good" multiplies the interval by the card's ease (2.5 to start),
	/// "hard" grows it by only 1.2 and lowers the ease, "easy" adds a bonus and raises it, and
	/// "again" sends the card back through relearning and lowers the ease. A card's ease never
	/// drops below 1.3. Anki's random fuzz is left out, so the schedule is predictable.
	/// </summary>
	public static class ReviewScheduler
	{
		public static readonly TimeSpan[] LearningSteps = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)];
		public static readonly TimeSpan[] RelearningSteps = [TimeSpan.FromMinutes(10)];

		public const double StartingEase = 2.5;
		public const double MinimumEase = 1.3;
		public const double GraduatingIntervalDays = 1;
		public const double EasyIntervalDays = 4;
		public const double EasyBonus = 1.3;
		public const double HardMultiplier = 1.2;
		public const double MaximumIntervalDays = 36500;

		/// <summary>How many new words a reader is introduced to per day — Anki's default.</summary>
		public const int NewCardsPerDay = 20;

		public static ReviewCard Rate(ReviewCard card, ReviewRating rating, DateTime nowUtc)
		{
			ReviewCard reviewed = card with
			{
				Reviews = card.Reviews + 1,
				FirstReviewedUtc = card.FirstReviewedUtc ?? nowUtc,
				LastReviewedUtc = nowUtc
			};

			return card.State switch
			{
				CardState.New or CardState.Learning => Learn(reviewed, rating, nowUtc, LearningSteps),
				CardState.Relearning => Learn(reviewed, rating, nowUtc, RelearningSteps),
				_ => Review(reviewed, rating, nowUtc)
			};
		}

		/// <summary>The learning and relearning steps, which count in minutes.</summary>
		private static ReviewCard Learn(ReviewCard card, ReviewRating rating, DateTime nowUtc, TimeSpan[] steps)
		{
			bool relearning = card.State == CardState.Relearning;
			CardState stepping = relearning ? CardState.Relearning : CardState.Learning;
			int step = Math.Min(card.Step, steps.Length - 1);

			switch (rating)
			{
				case ReviewRating.Again:
					return card with { State = stepping, Step = 0, DueUtc = nowUtc + steps[0] };

				case ReviewRating.Hard:
					// Repeats the step. On the first one Anki waits half again as long, or halfway
					// to the next step when there is one.
					TimeSpan delay = step == 0 && steps.Length > 1
						? (steps[0] + steps[1]) / 2
						: step == 0 ? steps[0] * 1.5 : steps[step];
					return card with { State = stepping, Step = step, DueUtc = nowUtc + delay };

				case ReviewRating.Good when step + 1 < steps.Length:
					return card with { State = stepping, Step = step + 1, DueUtc = nowUtc + steps[step + 1] };

				case ReviewRating.Good:
					// A relearnt card goes back to the interval its lapse left it; a new one to a day.
					return Graduate(card, relearning ? Math.Max(1, card.IntervalDays) : GraduatingIntervalDays, nowUtc);

				default:
					return Graduate(card, relearning ? Math.Max(1, card.IntervalDays) + 1 : EasyIntervalDays, nowUtc);
			}
		}

		private static ReviewCard Graduate(ReviewCard card, double intervalDays, DateTime nowUtc) =>
			card with
			{
				State = CardState.Review,
				Step = 0,
				IntervalDays = intervalDays,
				DueUtc = nowUtc + TimeSpan.FromDays(intervalDays)
			};

		private static ReviewCard Review(ReviewCard card, ReviewRating rating, DateTime nowUtc)
		{
			double interval = Math.Max(1, card.IntervalDays);
			// A card reviewed late was remembered for longer than asked, and gets credit for it.
			double daysLate = Math.Max(0, (nowUtc - card.DueUtc).TotalDays);

			if (rating == ReviewRating.Again)
			{
				return card with
				{
					State = CardState.Relearning,
					Step = 0,
					Lapses = card.Lapses + 1,
					Ease = Math.Max(MinimumEase, card.Ease - 0.2),
					// Anki's default "new interval" after a lapse is 0%, i.e. the one-day minimum.
					IntervalDays = 1,
					DueUtc = nowUtc + RelearningSteps[0]
				};
			}

			double hard = Math.Max(interval + 1, interval * HardMultiplier);
			double good = Math.Max(hard + 1, (interval + daysLate / 2) * card.Ease);
			double easy = Math.Max(good + 1, (interval + daysLate) * card.Ease * EasyBonus);

			(double next, double ease) = rating switch
			{
				ReviewRating.Hard => (hard, Math.Max(MinimumEase, card.Ease - 0.15)),
				ReviewRating.Good => (good, card.Ease),
				_ => (easy, card.Ease + 0.15)
			};

			next = Math.Min(MaximumIntervalDays, Math.Round(next, MidpointRounding.AwayFromZero));
			return card with
			{
				Ease = ease,
				IntervalDays = next,
				DueUtc = nowUtc + TimeSpan.FromDays(next)
			};
		}
	}
}
