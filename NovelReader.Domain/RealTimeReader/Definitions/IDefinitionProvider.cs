namespace NovelReader.Domain.RealTimeReader.Definitions
{
	/// <summary>
	/// A dictionary back end. Implementations must not throw for an ordinary miss — they
	/// return null so the caller can try the next provider (D1).
	/// </summary>
	public interface IDefinitionProvider
	{
		/// <summary>Human-readable name, surfaced to the reader as attribution.</summary>
		string Name { get; }

		Task<WordDefinition?> LookUpAsync(string term, CancellationToken cancellationToken = default);
	}
}
