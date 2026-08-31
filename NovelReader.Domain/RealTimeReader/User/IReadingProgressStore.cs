namespace NovelReader.Domain.RealTimeReader.User
{
	/// <summary>
	/// Per-reader, per-novel bookmarks.
	///
	/// Stored as one document per (user, novel) rather than a novels sub-document on the
	/// user. That shape is what makes "which novels has this reader read?" a plain
	/// <c>distinct</c> on an indexed field, and it keeps a bookmark write to a single
	/// document (D18).
	/// </summary>
	public interface IReadingProgressStore
	{
		/// <summary>Records where the reader is now, replacing any earlier bookmark.</summary>
		Task SaveAsync(string userName, ReadingProgress progress, CancellationToken cancellationToken = default);

		Task<ReadingProgress?> GetAsync(string userName, string novelName, CancellationToken cancellationToken = default);

		/// <summary>
		/// Novels this reader has opened, most recently read first, with whatever the catalogue
		/// last said about each of them.
		/// </summary>
		Task<IReadOnlyList<NovelSummary>> GetNovelsReadAsync(string userName, CancellationToken cancellationToken = default);

		/// <summary>
		/// Records what the catalogue said about one novel, and that it was asked.
		///
		/// <paramref name="summary"/> is null when the lookup found nothing: the timestamp is
		/// still written, so a novel the catalogue does not know about is retried once a day
		/// rather than on every request (D22).
		/// </summary>
		Task SaveNovelDetailsAsync(
			string userName,
			string novelName,
			NovelSummary? summary,
			DateTime checkedAtUtc,
			CancellationToken cancellationToken = default);

		/// <summary>The novel to reopen on arrival, or null for a reader with no history.</summary>
		Task<ReadingProgress?> GetMostRecentAsync(string userName, CancellationToken cancellationToken = default);
	}
}
