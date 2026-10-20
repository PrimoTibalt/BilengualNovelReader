using NovelReader.Domain.RealTimeReader.Review;

namespace NovelReader.Dictionary
{
	/// <summary>
	/// The review's quick judge (D46): one Jev <c>choice</c> question per answer, its options the
	/// three verdicts, each described by the same rule the AI's prompt gives it.
	/// </summary>
	internal sealed class JevAnswerJudge(JevClient jev) : IAnswerJudge
	{
		/// <summary>
		/// Below this, Jev is torn between verdicts, and the answer is better left to the reader's
		/// AI, which can also say what was wrong with it.
		/// </summary>
		internal const double MinimumConfidence = 0.6;

		private static readonly IReadOnlyDictionary<AnswerVerdict, string> OptionNames = new Dictionary<AnswerVerdict, string>
		{
			[AnswerVerdict.Correct] = "correct",
			[AnswerVerdict.Partial] = "partial",
			[AnswerVerdict.Wrong] = "wrong"
		};

		public async Task<AnswerVerdict?> JudgeAsync(
			string instructions,
			IReadOnlyDictionary<AnswerVerdict, string> verdicts,
			string answer,
			CancellationToken cancellationToken = default)
		{
			Dictionary<string, string> options = verdicts.ToDictionary(verdict => OptionNames[verdict.Key], verdict => verdict.Value);
			return VerdictFor(await jev.ChooseAsync(answer, instructions, options, cancellationToken));
		}

		/// <summary>The verdict Jev chose, or null when it chose nothing known or was not sure.</summary>
		internal static AnswerVerdict? VerdictFor(JevChoice? choice)
		{
			if (choice is null || choice.Probability < MinimumConfidence)
			{
				return null;
			}

			return OptionNames
				.Where(option => option.Value == choice.Option)
				.Select(option => (AnswerVerdict?)option.Key)
				.FirstOrDefault();
		}
	}
}
