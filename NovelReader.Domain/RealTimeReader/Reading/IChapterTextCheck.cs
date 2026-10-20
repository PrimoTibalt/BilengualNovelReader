namespace NovelReader.Domain.RealTimeReader.Reading
{
	/// <summary>
	/// A second look at a freshly scraped chapter before it is cached (D47): is it a chapter at
	/// all, or a page of the site's own — a notice, an error, a paywall, a browser check — that
	/// happened to land in the chapter's place? <c>ParagraphsRetriever</c> already recognises the
	/// "page moved" notice by its wording (D21); this catches the ones nobody has seen yet.
	/// </summary>
	public interface IChapterTextCheck
	{
		/// <returns>
		/// True only when the text is confidently not a chapter. A check that fails or is unsure
		/// answers false: refusing a real chapter would lose it, caching junk only costs a cleanup.
		/// </returns>
		Task<bool> IsSitePageAsync(
			IReadOnlyDictionary<int, string> paragraphs,
			CancellationToken cancellationToken = default);
	}
}
