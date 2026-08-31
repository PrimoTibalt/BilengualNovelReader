using NovelReader.Domain.RealTimeReader.Definitions;

namespace NovelReader.Dictionary
{
	/// <summary>
	/// Tries each provider in order and returns the first real answer. A provider that
	/// misses or fails returns null rather than throwing, so a slow or broken back end
	/// degrades into "no definition yet" instead of an error (D1).
	/// </summary>
	internal class FallbackDefinitionProvider(IReadOnlyList<IDefinitionProvider> providers)
		: IDefinitionProvider
	{
		public string Name => "Fallback";

		public async Task<WordDefinition?> LookUpAsync(string term, CancellationToken cancellationToken = default)
		{
			foreach (IDefinitionProvider provider in providers)
			{
				cancellationToken.ThrowIfCancellationRequested();

				WordDefinition? definition = await provider.LookUpAsync(term, cancellationToken);
				if (definition is not null && definition.HasSenses)
				{
					return definition;
				}
			}

			return null;
		}
	}
}
