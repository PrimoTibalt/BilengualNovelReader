using NovelReader.Domain.RealTimeReader.Definitions;

namespace NovelReader.Domain.RealTimeReader.Assistant
{
	/// <summary>
	/// A definition from the reader's own AI when they have set one up, read against the
	/// paragraph the word was selected in; from the dictionaries otherwise (D35), with the sense
	/// the paragraph uses moved to the front when there is a <c>senseChooser</c> (D47).
	///
	/// An AI answer is never cached. It is about this word *in this paragraph*, for this reader's
	/// model — putting it in the shared definition cache (D2) would hand one reader's contextual
	/// reading to everyone else as the word's meaning. The chosen sense is not cached for the
	/// same reason: the cache keeps the dictionary's order, and the order is decided per request.
	/// </summary>
	public sealed class AssistedDefinitionService(
		AssistantSettingsService assistantSettings,
		IAssistant assistant,
		DefinitionLookupService dictionary,
		ISenseChooser? senseChooser = null)
	{
		/// <summary>
		/// What the box pages through. The dictionaries keep more than this, so that the sense a
		/// paragraph uses can be found even when it is listed late — *bank* by a river is the
		/// 16th of 42 — but the reader is shown the first dozen.
		/// </summary>
		public const int MaximumShownSenses = 12;

		public async Task<WordDefinition?> LookUpAsync(
			string userName,
			string term,
			string? paragraph,
			CancellationToken cancellationToken = default)
		{
			AssistantSettings? settings = await assistantSettings.GetAsync(userName, cancellationToken);
			if (settings is not null)
			{
				WordDefinition? answered = await assistant.DefineAsync(settings, term, paragraph, cancellationToken);
				if (answered is { HasSenses: true })
				{
					return answered;
				}

				// A wrong key or a model that is down should not leave the reader with nothing.
				// The box names its source, so the dictionary answering is visible, not silent.
			}

			WordDefinition? found = await dictionary.LookUpAsync(term, cancellationToken);
			if (found is null)
			{
				return null;
			}

			int chosen = await ChosenSenseAsync(term, paragraph, found.Senses, cancellationToken);
			List<DefinitionSense> senses = [.. found.Senses];
			if (chosen > 0)
			{
				senses.RemoveAt(chosen);
				senses.Insert(0, found.Senses[chosen]);
			}

			return new WordDefinition
			{
				Term = found.Term,
				Senses = senses.Count > MaximumShownSenses ? senses[..MaximumShownSenses] : senses,
				SourceName = found.SourceName,
				SourceUrl = found.SourceUrl
			};
		}

		/// <summary>The index of the sense the paragraph uses; 0, the dictionary's first, when nothing better is known.</summary>
		private async Task<int> ChosenSenseAsync(
			string term,
			string? paragraph,
			IReadOnlyList<DefinitionSense> senses,
			CancellationToken cancellationToken)
		{
			if (senseChooser is null || string.IsNullOrWhiteSpace(paragraph) || senses.Count < 2)
			{
				return 0;
			}

			int? chosen = await senseChooser.ChooseAsync(term, paragraph, senses, cancellationToken);
			return chosen is { } index && index >= 0 && index < senses.Count ? index : 0;
		}
	}
}
