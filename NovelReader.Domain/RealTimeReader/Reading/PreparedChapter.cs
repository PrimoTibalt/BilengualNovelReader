namespace NovelReader.Domain.RealTimeReader.Reading
{
	/// <summary>
	/// A chapter whose paragraphs have already been marked up for one particular reader.
	/// The markup depends on that reader's vocabulary, which is why the cache is
	/// user-scoped (D8).
	/// </summary>
	public class PreparedChapter
	{
		public required string NovelName { get; init; }

		public required int ChapterNumber { get; init; }

		/// <summary>Paragraph number to marked-up HTML, as produced by the markup builder.</summary>
		public required IReadOnlyDictionary<int, string> Paragraphs { get; init; }

		public required DateTime PreparedAtUtc { get; init; }

		public bool TryGetParagraph(int paragraphNumber, out string paragraph)
		{
			if (Paragraphs.TryGetValue(paragraphNumber, out string? found))
			{
				paragraph = found;
				return true;
			}

			paragraph = string.Empty;
			return false;
		}
	}
}
