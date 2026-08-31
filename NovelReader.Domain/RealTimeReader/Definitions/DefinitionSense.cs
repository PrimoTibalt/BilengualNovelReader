namespace NovelReader.Domain.RealTimeReader.Definitions
{
	/// <summary>
	/// One sense of a word. A term usually has several; the reader pages through them
	/// with j/k.
	/// </summary>
	public class DefinitionSense
	{
		public string? PartOfSpeech { get; init; }

		public required string Text { get; init; }

		public string? Example { get; init; }
	}
}
