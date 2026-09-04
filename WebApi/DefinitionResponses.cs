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
	/// Answer to a translation request. Exactly one of <see cref="Text"/> and
	/// <see cref="Error"/> is set.
	///
	/// <see cref="Error"/> is a code rather than a sentence, because the page acts on it as
	/// well as showing it: <c>not-configured</c> opens the settings form rather than reporting
	/// a failure, while the others are shown to the reader (D31).
	///
	/// <see cref="SurfaceForm"/> is what the reader selected, echoed back so the page can tell
	/// which box an answer belongs to. It cannot use <see cref="Term"/> for that: a translation
	/// is now asked for alongside the definition rather than after it (D32), so when the answer
	/// lands the page may not yet know what the term normalises to.
	/// </summary>
	public record TranslationResponse(string Term, string SurfaceForm, string? Text, string? TargetLanguage, string? Error)
	{
		public const string NotConfigured = "not-configured";
		public const string SettingsInvalid = "settings-invalid";
		public const string Unavailable = "unavailable";

		public static TranslationResponse Failed(string term, string surfaceForm, string error) =>
			new(term, surfaceForm, Text: null, TargetLanguage: null, error);
	}

	/// <summary>One language the reader may translate into, for the settings form.</summary>
	public record TranslationLanguageResponse(string Code, string Name);

	/// <summary>
	/// Answer to a settings save. <see cref="Error"/> null means stored; otherwise it names the
	/// field the reader has to fix, so the form can say which one.
	/// </summary>
	public record TranslationSettingsResponse(string? Email, string? Language, string? Error)
	{
		public const string EmailInvalid = "email-invalid";
		public const string LanguageInvalid = "language-invalid";
	}

	/// <summary>Sent after a save or delete so the page can update underlines in place.</summary>
	public record VocabularyChangedResponse(string Term, bool IsSaved);
}
