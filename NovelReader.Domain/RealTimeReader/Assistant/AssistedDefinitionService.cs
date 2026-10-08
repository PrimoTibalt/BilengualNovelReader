using NovelReader.Domain.RealTimeReader.Definitions;

namespace NovelReader.Domain.RealTimeReader.Assistant
{
	/// <summary>
	/// A definition from the reader's own AI when they have set one up, read against the
	/// paragraph the word was selected in; from the dictionaries otherwise (D35).
	///
	/// An AI answer is never cached. It is about this word *in this paragraph*, for this reader's
	/// model — putting it in the shared definition cache (D2) would hand one reader's contextual
	/// reading to everyone else as the word's meaning.
	/// </summary>
	public sealed class AssistedDefinitionService(
		AssistantSettingsService assistantSettings,
		IAssistant assistant,
		DefinitionLookupService dictionary)
	{
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

			return await dictionary.LookUpAsync(term, cancellationToken);
		}
	}
}
