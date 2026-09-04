namespace NovelReader.Domain.RealTimeReader.Translation
{
	/// <summary>
	/// A translation service. Mirrors <c>IDefinitionProvider</c> (D1): a provider that is down,
	/// slow or out of quota answers null rather than throwing, because a failed translation
	/// must never break the reading session.
	/// </summary>
	public interface ITranslationProvider
	{
		string Name { get; }

		/// <summary>
		/// Translates from English into <paramref name="targetLanguage"/>.
		/// <paramref name="contactEmail"/> is passed to the provider to claim the reader's own
		/// allowance.
		/// </summary>
		Task<Translation?> TranslateAsync(
			string text,
			string targetLanguage,
			string contactEmail,
			CancellationToken cancellationToken = default);
	}
}
