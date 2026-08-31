namespace NovelReader.Domain.RealTimeReader.Parsing
{
	/// <summary>
	/// Builds the paths <see cref="ISearchNovelsRetriever"/> is given. One place, because the
	/// hub and the library refresher both need it, and a search built two ways is a search that
	/// works one way.
	/// </summary>
	public static class NovelSearchQuery
	{
		/// <summary>
		/// The catalogue's live-search endpoint. It is GET-only and the parameter is
		/// <c>keyword</c> — <c>q</c>, <c>inputContent</c> and the rest answer with null data.
		/// </summary>
		public static string PathFor(string keyword)
		{
			return $"ajax/searchLive?keyword={Uri.EscapeDataString(keyword)}";
		}

		/// <summary>
		/// A searchable name for a novel we only know the slug of. Slugs are the title
		/// hyphenated, so "reverend-insanity" searches as "reverend insanity".
		/// </summary>
		public static string KeywordFromSlug(string slug)
		{
			return slug.Replace('-', ' ').Trim();
		}
	}
}
