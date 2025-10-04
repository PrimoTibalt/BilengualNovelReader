namespace NovelReader.Domain.RealTimeReader.Parsing
{
	public class ParagraphData
	{
		public required string BookName { get; set; }
		public required int ChapterNumber { get; set; }
		public required int ParagraphNumber { get; set; }
		public required string Content { get; set; }

		public void Deconstruct(out int chapterNumber, out int paragraphNumber, out string content)
		{
			chapterNumber = ChapterNumber;
			paragraphNumber = ParagraphNumber;
			content = Content;
		}
	}
}
