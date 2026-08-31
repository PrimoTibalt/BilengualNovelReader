namespace NovelReader.Domain.RealTimeReader.Vocabulary
{
	/// <summary>
	/// A word or phrase the reader has met before and chosen to keep.
	/// </summary>
	public class VocabularyEntry
	{
		/// <summary>Lookup key, produced by <see cref="TermNormalizer"/>.</summary>
		public required string NormalizedTerm { get; init; }

		/// <summary>The form the reader actually selected, kept for display.</summary>
		public required string SurfaceForm { get; init; }

		/// <summary>The novel it was first met in. Matching is global; this is provenance.</summary>
		public required string NovelName { get; init; }

		public required DateTime SavedAtUtc { get; init; }
	}
}
