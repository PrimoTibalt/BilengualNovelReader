namespace NovelReader.Domain
{
	public interface IChapter
	{
		int ChapterNumber { get; init; }
		Dictionary<string, object> Paragraphs { get; init; }
		bool TryGetParagraph(int paragraphNumber, out string paragraph);
	}
}
