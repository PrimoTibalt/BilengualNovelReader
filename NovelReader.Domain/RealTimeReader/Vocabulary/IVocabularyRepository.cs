namespace NovelReader.Domain.RealTimeReader.Vocabulary
{
	/// <summary>
	/// Per-user store of previously encountered words. The user name is threaded through
	/// every call rather than ambient, so real identity can replace it later (D9).
	/// </summary>
	public interface IVocabularyRepository
	{
		Task AddAsync(string userName, VocabularyEntry entry, CancellationToken cancellationToken = default);

		Task RemoveAsync(string userName, string normalizedTerm, CancellationToken cancellationToken = default);

		Task<bool> ContainsAsync(string userName, string normalizedTerm, CancellationToken cancellationToken = default);

		/// <summary>
		/// Every term the user has saved. The markup builder needs the whole set at once to
		/// tag a chapter, so this is deliberately a bulk read rather than per-word probes.
		/// </summary>
		Task<IReadOnlyCollection<VocabularyEntry>> GetAllForUserAsync(string userName, CancellationToken cancellationToken = default);
	}
}
