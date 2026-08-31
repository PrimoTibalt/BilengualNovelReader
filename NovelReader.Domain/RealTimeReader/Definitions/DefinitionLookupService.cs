using NovelReader.Domain.RealTimeReader.Vocabulary;

namespace NovelReader.Domain.RealTimeReader.Definitions
{
	/// <summary>
	/// Cache-first lookup: consult the cache, ask a provider only on a miss, then record
	/// the outcome — including the fact that nothing knows the word, so typos and proper
	/// nouns are not re-fetched on every encounter (D2).
	/// </summary>
	public class DefinitionLookupService(IDefinitionProvider definitionProvider, IDefinitionCache definitionCache)
	{
		public async Task<WordDefinition?> LookUpAsync(string? surfaceForm, CancellationToken cancellationToken = default)
		{
			string normalizedTerm = TermNormalizer.Normalize(surfaceForm);
			if (normalizedTerm.Length == 0)
			{
				return null;
			}

			DefinitionLookupResult cached = await definitionCache.TryGetAsync(normalizedTerm, cancellationToken);
			switch (cached.Status)
			{
				case DefinitionCacheStatus.Found:
					return cached.Definition;
				case DefinitionCacheStatus.KnownMissing:
					return null;
			}

			WordDefinition? definition = await definitionProvider.LookUpAsync(normalizedTerm, cancellationToken);
			if (definition is null || !definition.HasSenses)
			{
				await definitionCache.StoreMissAsync(normalizedTerm, cancellationToken);
				return null;
			}

			// Store under the normalised term so every surface form shares one cache entry.
			WordDefinition toCache = new()
			{
				Term = normalizedTerm,
				Senses = definition.Senses,
				SourceName = definition.SourceName,
				SourceUrl = definition.SourceUrl
			};

			await definitionCache.StoreAsync(toCache, cancellationToken);
			return toCache;
		}
	}
}
