using NovelReader.Domain.RealTimeReader.Definitions;

namespace NovelReader.Dictionary
{
	/// <summary>
	/// The sense a word has in its paragraph, as one Jev <c>choice</c> over the dictionary's
	/// senses (D47). Options are the senses' numbers, plus <c>none</c> so that a word whose
	/// meaning is not listed is not forced onto the nearest wrong one.
	/// </summary>
	internal sealed class JevSenseChooser(JevClient jev) : ISenseChooser
	{
		/// <summary>
		/// Measured: right picks came back at 0.58 and above. Below this the dictionary's own
		/// order, which puts the commonest sense first, is the better guess.
		/// </summary>
		internal const double MinimumConfidence = 0.5;

		/// <summary>As for the reader's AI: the paragraph is context, and a page-long one is not worth paying for.</summary>
		private const int MaximumParagraphLength = OpenAiCompatibleAssistant.MaximumParagraphLength;

		/// <summary>A sense is a line or two; a very long one is cut rather than inflating every lookup.</summary>
		private const int MaximumSenseLength = 300;

		private const string NoneOption = "none";

		public async Task<int?> ChooseAsync(
			string term,
			string paragraph,
			IReadOnlyList<DefinitionSense> senses,
			CancellationToken cancellationToken = default)
		{
			Dictionary<string, string> options = [];
			for (int index = 0; index < senses.Count; index++)
			{
				DefinitionSense sense = senses[index];
				string text = sense.Text.Length > MaximumSenseLength ? sense.Text[..MaximumSenseLength] : sense.Text;
				options[(index + 1).ToString()] = sense.PartOfSpeech is null ? text : $"({sense.PartOfSpeech.ToLowerInvariant()}) {text}";
			}

			options[NoneOption] = "none of these senses is the one meant here";

			string context = paragraph.Length > MaximumParagraphLength ? paragraph[..MaximumParagraphLength] : paragraph;
			JevChoice? choice = await jev.ChooseAsync(
				$"Word: {term}\nParagraph: {context}",
				$"Someone reading a novel selected the word \"{term}\" in this paragraph. Which dictionary sense is the word used in here?",
				options,
				cancellationToken);

			return IndexFor(choice, senses.Count);
		}

		/// <summary>The zero-based sense Jev picked, or null for <c>none</c>, an unknown option or an unsure pick.</summary>
		internal static int? IndexFor(JevChoice? choice, int senseCount) =>
			choice is not null
				&& choice.Probability >= MinimumConfidence
				&& int.TryParse(choice.Option, out int number)
				&& number >= 1
				&& number <= senseCount
					? number - 1
					: null;
	}
}
