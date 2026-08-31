namespace NovelReader.Domain.RealTimeReader.Definitions
{
	/// <summary>
	/// A looked-up term with its senses in the order they should be presented.
	/// </summary>
	public class WordDefinition
	{
		public required string Term { get; init; }

		public required IReadOnlyList<DefinitionSense> Senses { get; init; }

		/// <summary>Which provider answered. Shown in the box; Wiktionary is CC BY-SA (D1).</summary>
		public required string SourceName { get; init; }

		public string? SourceUrl { get; init; }

		public bool HasSenses => Senses.Count > 0;
	}
}
