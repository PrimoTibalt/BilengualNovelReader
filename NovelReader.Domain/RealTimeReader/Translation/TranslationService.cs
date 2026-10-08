using NovelReader.Domain.RealTimeReader.Assistant;

namespace NovelReader.Domain.RealTimeReader.Translation
{
	public enum TranslationFailure
	{
		None,
		/// <summary>This reader has no email and language yet, so there is nothing to ask with.</summary>
		NotConfigured,
		/// <summary>Settings arrived, but they do not pass the same rules a save would.</summary>
		SettingsInvalid,
		/// <summary>The provider was down, slow, or out of allowance.</summary>
		Unavailable
	}

	public sealed record TranslationOutcome(Translation? Translation, TranslationFailure Failure)
	{
		public static TranslationOutcome Ok(Translation translation) => new(translation, TranslationFailure.None);
		public static TranslationOutcome Failed(TranslationFailure failure) => new(null, failure);
	}

	/// <summary>
	/// Turns "translate this" into a provider call, once the reader's settings are known.
	///
	/// Settings can arrive two ways. Normally they are read from the reader's record. But the
	/// very first translation is sent at the same moment as the save that stores them, so that
	/// one call carries them itself (D31) — otherwise the two race and the first translation a
	/// reader ever asks for is the one that fails.
	///
	/// A reader who has set an AI up is translated by it instead, with the paragraph the
	/// selection came from as context (D35). The AI only needs the target language; if it
	/// fails, the provider is tried as before, so a bad key degrades rather than breaks.
	/// </summary>
	public sealed class TranslationService(
		ITranslationSettingsStore store,
		ITranslationProvider provider,
		AssistantSettingsService assistantSettings,
		IAssistant assistant)
	{
		public async Task<TranslationOutcome> TranslateAsync(
			string userName,
			string text,
			string? emailOverride = null,
			string? languageOverride = null,
			string? paragraph = null,
			CancellationToken cancellationToken = default)
		{
			AssistantSettings? aiSettings = await assistantSettings.GetAsync(userName, cancellationToken);
			if (aiSettings is not null)
			{
				string? language = TranslationLanguages.Normalize(languageOverride)
					?? (await store.GetAsync(userName, cancellationToken))?.TargetLanguage;

				TranslationLanguage? target = TranslationLanguages.All.FirstOrDefault(candidate => candidate.Code == language);
				if (target is null)
				{
					return TranslationOutcome.Failed(TranslationFailure.NotConfigured);
				}

				string? translated = await assistant.TranslateAsync(
					aiSettings, text, paragraph, target.Name, cancellationToken);
				if (translated is not null)
				{
					return TranslationOutcome.Ok(new Translation(translated, target.Code, aiSettings.Model));
				}
			}

			TranslationSettings? settings;

			if (!string.IsNullOrWhiteSpace(emailOverride) || !string.IsNullOrWhiteSpace(languageOverride))
			{
				// Supplied by a client that has just filled the form in. Held to the same rules
				// as a save, so the request cannot smuggle past what the form would refuse.
				if (TranslationSettingsValidator.Validate(emailOverride, languageOverride) != SettingsFailure.None)
				{
					return TranslationOutcome.Failed(TranslationFailure.SettingsInvalid);
				}

				settings = TranslationSettingsValidator.Normalize(emailOverride!, languageOverride!);
			}
			else
			{
				settings = await store.GetAsync(userName, cancellationToken);
			}

			if (settings is null)
			{
				return TranslationOutcome.Failed(TranslationFailure.NotConfigured);
			}

			Translation? translation = await provider.TranslateAsync(
				text, settings.TargetLanguage, settings.Email, cancellationToken);

			return translation is null
				? TranslationOutcome.Failed(TranslationFailure.Unavailable)
				: TranslationOutcome.Ok(translation);
		}
	}
}
