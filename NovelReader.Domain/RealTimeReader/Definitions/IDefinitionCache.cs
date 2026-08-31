namespace NovelReader.Domain.RealTimeReader.Definitions
{
	/// <summary>
	/// Global (not per-user) definition cache — "ephemeral" means the same for everyone.
	/// Only the vocabulary is per-user. See D2.
	/// </summary>
	public interface IDefinitionCache
	{
		Task<DefinitionLookupResult> TryGetAsync(string normalizedTerm, CancellationToken cancellationToken = default);

		Task StoreAsync(WordDefinition definition, CancellationToken cancellationToken = default);

		/// <summary>Remember that no provider knows this term, so typos are not re-fetched.</summary>
		Task StoreMissAsync(string normalizedTerm, CancellationToken cancellationToken = default);
	}
}
