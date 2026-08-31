namespace NovelReader.Domain.RealTimeReader.Reading
{
	/// <summary>
	/// Two-tier cache for prepared chapters (D8). The durable MongoDB tier holds the chapter
	/// the reader is about to reach; the in-memory tier holds the one they are reading now.
	/// The tiers are addressed separately because reaching a chapter that was waiting in the
	/// durable tier is exactly the moment to promote it and prefetch the one after.
	/// </summary>
	public interface IPreparedChapterCache
	{
		Task<PreparedChapter?> TryGetFromMemoryAsync(
			string userName,
			string novelName,
			int chapterNumber,
			CancellationToken cancellationToken = default);

		/// <summary>Reads the durable tier, keyed {user}/{novel}/chapter{N}.</summary>
		Task<PreparedChapter?> TryGetFromDurableAsync(
			string userName,
			string novelName,
			int chapterNumber,
			CancellationToken cancellationToken = default);

		Task StoreInDurableAsync(
			string userName,
			PreparedChapter chapter,
			CancellationToken cancellationToken = default);

		/// <summary>
		/// Moves a chapter into memory and drops it from the durable tier, which then makes
		/// room for the next chapter to be prefetched into it.
		/// </summary>
		Task PromoteToMemoryAsync(
			string userName,
			PreparedChapter chapter,
			CancellationToken cancellationToken = default);

		/// <summary>
		/// Drops everything cached for a user. Called when their vocabulary changes, because
		/// underlines are baked into the stored markup (F5).
		/// </summary>
		Task InvalidateForUserAsync(string userName, CancellationToken cancellationToken = default);
	}
}
