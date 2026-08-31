namespace NovelReader
{
	/// <summary>Shapes sent to the reading page over SignalR.</summary>
	public record DefinitionSenseResponse(string? PartOfSpeech, string Text, string? Example);

	public record DefinitionResponse(
		string Term,
		string SurfaceForm,
		IReadOnlyList<DefinitionSenseResponse> Senses,
		string? SourceName,
		string? SourceUrl,
		bool IsSaved,
		bool Found);

	/// <summary>
	/// Answer to a translation request. No provider is configured yet (D10), so the hub
	/// returns <see cref="Stub"/> — <c>IsStub</c> is what stops the page presenting a
	/// placeholder as if it were a real translation.
	/// </summary>
	public record TranslationResponse(string Term, string Text, string? TargetLanguage, bool IsStub)
	{
		/// <summary>
		/// A fixed answer that exercises the whole round trip: press <c>t</c>, the hub replies,
		/// the definition box shows it. Swapping in a real provider means replacing this call,
		/// not rewiring the client.
		/// </summary>
		public static TranslationResponse Stub(string term) => new(
			term,
			$"“{term}” — placeholder translation sent by the server.",
			TargetLanguage: null,
			IsStub: true);
	}

	/// <summary>Sent after a save or delete so the page can update underlines in place.</summary>
	public record VocabularyChangedResponse(string Term, bool IsSaved);
}
