namespace NovelReader
{
	/// <summary>One paragraph of a chapter, with the number it actually has.</summary>
	public record ParagraphResponse(int Number, string Markup);

	/// <summary>
	/// A whole chapter. Sending the chapter in one message is what lets the page restore a
	/// reader's position without a request per paragraph (D19). Not found with no
	/// <see cref="Failure"/> is the end of what is published; with <c>"unavailable"</c>, the site
	/// did not serve it and the page offers a retry (D49).
	/// </summary>
	public record ChapterResponse(
		string NovelName,
		int ChapterNumber,
		IReadOnlyList<ParagraphResponse> Paragraphs,
		bool Found,
		string? Failure = null);

	/// <summary>
	/// One hit from a novel search. <see cref="Slug"/> is the name every other call uses, so
	/// picking a result is enough to open it. Rank and chapter count are optional because the
	/// source does not always give them.
	/// </summary>
	public record NovelSearchResponse(string Title, string Slug, int? Rank, int? TotalChapters);

	/// <summary>
	/// A novel in the reader's library. <see cref="Title"/>, <see cref="Rank"/> and
	/// <see cref="TotalChapters"/> are the catalogue's, cached beside the bookmark and
	/// refreshed daily (D22) — any of them may be null for a novel not looked up yet, so the
	/// page falls back to the slug.
	/// </summary>
	public record NovelSummaryResponse(string Slug, string? Title, int? Rank, int? TotalChapters);

	/// <summary>
	/// The first thing the reading page is told: which novels this reader has open, and where
	/// to put them back. <see cref="Novels"/> comes from their stored progress, so a new
	/// reader gets the default novel and an empty list until they read something.
	/// </summary>
	public record ReadingSessionResponse(
		string UserName,
		IReadOnlyList<NovelSummaryResponse> Novels,
		string NovelName,
		int ChapterNumber,
		int ParagraphNumber,
		bool Resuming,
		/// <summary>
		/// The reader's translation settings, both null until they have set them up — which is
		/// how the page knows that `t` should ask rather than translate (D31).
		/// </summary>
		string? TranslationEmail,
		string? TranslationLanguage,
		/// <summary>
		/// The languages the form offers. Sent with the session rather than fetched when the
		/// form opens: it is under a kilobyte, and it means the form has nothing to wait for.
		/// </summary>
		IReadOnlyList<TranslationLanguageResponse> TranslationLanguages,
		/// <summary>
		/// The reader's AI, for the settings panel and so the page knows to send the paragraph
		/// with every lookup (D35). Never carries the key.
		/// </summary>
		AssistantSettingsResponse Assistant);

	/// <summary>One chapter of an uploaded book, as its table of contents names it (D50).</summary>
	/// <param name="Extra">Not part of the book itself — copyright, afterword, newsletter (D52).</param>
	public record BookChapterResponse(int Number, string Title, bool Extra);

	/// <summary>An uploaded book's table of contents (D50).</summary>
	public record BookContentsResponse(string Slug, string Title, string? Author, IReadOnlyList<BookChapterResponse> Chapters);
}
