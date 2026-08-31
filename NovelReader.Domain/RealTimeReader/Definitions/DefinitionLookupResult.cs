namespace NovelReader.Domain.RealTimeReader.Definitions
{
	public enum DefinitionCacheStatus
	{
		/// <summary>Never looked up; the caller should ask a provider.</summary>
		Unknown = 0,

		/// <summary>Cached definition available.</summary>
		Found = 1,

		/// <summary>Looked up before and genuinely not a word; do not re-fetch yet (D2).</summary>
		KnownMissing = 2
	}

	public class DefinitionLookupResult
	{
		public required DefinitionCacheStatus Status { get; init; }

		public WordDefinition? Definition { get; init; }

		public static DefinitionLookupResult Unknown { get; } =
			new() { Status = DefinitionCacheStatus.Unknown };

		public static DefinitionLookupResult Missing { get; } =
			new() { Status = DefinitionCacheStatus.KnownMissing };

		public static DefinitionLookupResult Hit(WordDefinition definition) =>
			new() { Status = DefinitionCacheStatus.Found, Definition = definition };
	}
}
