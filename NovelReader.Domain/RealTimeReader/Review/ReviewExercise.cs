namespace NovelReader.Domain.RealTimeReader.Review
{
	/// <summary>
	/// The kinds of exercise, easiest first. A word starts on <see cref="Match"/> and moves on as
	/// its interval grows (see <see cref="ReviewPlanner.KindFor"/>).
	/// </summary>
	public enum ExerciseKind
	{
		/// <summary>Join words to their one-line definitions.</summary>
		Match,
		/// <summary>Say what a word means, in the reader's own words; the AI judges it.</summary>
		Recall,
		/// <summary>Make a sentence with the word; the AI judges the usage.</summary>
		Sentence,
		/// <summary>Translate a sentence from the reader's language into English, using the word.</summary>
		Translate
	}

	/// <summary>An item on one side of a match: what is shown, and the id an answer names it by.</summary>
	public sealed record MatchItem(string Id, string Text);

	/// <summary>
	/// One exercise as the page shows it. A match fills <see cref="Words"/> and
	/// <see cref="Definitions"/>; the others fill <see cref="Prompt"/> — the word, or for a
	/// translation the sentence to translate — and leave the lists empty. Nothing here gives an
	/// answer away: which definition belongs to which word stays on the server.
	/// </summary>
	public sealed record ReviewExercise(
		string Id,
		ExerciseKind Kind,
		IReadOnlyList<MatchItem> Words,
		IReadOnlyList<MatchItem> Definitions,
		string? Prompt,
		string? LanguageName,
		int Remaining);

	/// <summary>Where a word's schedule stands after it was graded.</summary>
	public sealed record GradedWord(string Word, ReviewRating Rating, DateTime NextDueUtc);

	/// <summary>
	/// The answer to one attempted pair. <see cref="Graded"/> is filled in once every pair is
	/// matched and the round has been scored.
	/// </summary>
	public sealed record MatchCheck(bool Correct, bool Completed, IReadOnlyList<GradedWord> Graded);

	/// <summary>How the AI judged a free answer.</summary>
	public enum AnswerVerdict
	{
		Correct,
		Partial,
		Wrong
	}

	/// <summary>
	/// The outcome of a free answer: the verdict and what the AI said about it, the meaning (or
	/// for a translation, an English rendering) to compare with, and the word's new schedule.
	/// </summary>
	public sealed record AnswerOutcome(
		AnswerVerdict Verdict,
		string Feedback,
		string Reference,
		GradedWord Graded);

	/// <summary>Why an exercise call could not be served.</summary>
	public enum ReviewFailure
	{
		None,
		/// <summary>The review needs the reader's AI, and none is set up.</summary>
		AssistantRequired,
		/// <summary>The exercise asked about is not the one the reader has open.</summary>
		ExerciseUnknown,
		/// <summary>The AI did not answer usefully; nothing was graded and it can be tried again.</summary>
		AssistantUnavailable,
		/// <summary>An empty answer, or one far longer than any sentence.</summary>
		AnswerInvalid
	}

	/// <summary>What is waiting: due now, new words left for today, and when the next one comes due.</summary>
	public sealed record ReviewSummary(int Due, int New, int Total, DateTime? NextDueUtc);
}
