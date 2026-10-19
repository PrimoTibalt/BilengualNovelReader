using NovelReader.Domain.RealTimeReader.Review;

namespace NovelReader
{
	/// <summary>
	/// The review's wire shapes. Kinds, verdicts and ratings travel as lower-case words and
	/// failures as codes, like every other refusal the hub sends; the page owns the wording.
	/// </summary>
	public static class ReviewCodes
	{
		public static string? Failure(ReviewFailure failure) => failure switch
		{
			ReviewFailure.None => null,
			ReviewFailure.AssistantRequired => "ai-required",
			ReviewFailure.ExerciseUnknown => "exercise-unknown",
			ReviewFailure.AssistantUnavailable => "ai-unavailable",
			_ => "answer-invalid"
		};

		public static string Word<TEnum>(TEnum value) where TEnum : struct, Enum => value.ToString().ToLowerInvariant();
	}

	public record ReviewSummaryResponse(int Due, int New, int Total, DateTime? NextDueUtc, string? Error);

	public record MatchItemResponse(string Id, string Text);

	/// <summary>
	/// The next exercise, or — with <see cref="Kind"/> null and no error — nothing due, in which
	/// case <see cref="NextDueUtc"/> says when something will be.
	/// </summary>
	public record ReviewExerciseResponse(
		string? Id,
		string? Kind,
		IReadOnlyList<MatchItemResponse> Words,
		IReadOnlyList<MatchItemResponse> Definitions,
		string? Prompt,
		string? LanguageName,
		int Remaining,
		DateTime? NextDueUtc,
		string? Error);

	public record GradedWordResponse(string Word, string Rating, DateTime NextDueUtc)
	{
		public static GradedWordResponse From(GradedWord graded) =>
			new(graded.Word, ReviewCodes.Word(graded.Rating), graded.NextDueUtc);
	}

	public record MatchCheckResponse(bool Correct, bool Completed, IReadOnlyList<GradedWordResponse> Graded, string? Error);

	public record AnswerOutcomeResponse(
		string? Verdict,
		string? Feedback,
		string? Reference,
		GradedWordResponse? Graded,
		string? Error);

	/// <summary><see cref="Solution"/> maps a match's word ids to their definition ids.</summary>
	public record GiveUpResponse(
		string? Reference,
		IReadOnlyDictionary<string, string> Solution,
		IReadOnlyList<GradedWordResponse> Graded,
		string? Error);
}
