namespace NovelReader.Domain.RealTimeReader.Review
{
	/// <summary>
	/// A fast, cheap classifier that picks a verdict for a free answer without writing anything
	/// about it (D46). The review asks it before the reader's AI; null — no answer, a failure, or
	/// not sure enough — sends the answer on to the AI, which judges it and explains.
	/// </summary>
	public interface IAnswerJudge
	{
		/// <param name="instructions">What is being judged, in plain words.</param>
		/// <param name="verdicts">What each verdict means for this kind of exercise.</param>
		/// <param name="answer">The exercise and the reader's answer, as text.</param>
		Task<AnswerVerdict?> JudgeAsync(
			string instructions,
			IReadOnlyDictionary<AnswerVerdict, string> verdicts,
			string answer,
			CancellationToken cancellationToken = default);
	}
}
