namespace NovelReader.Domain.RealTimeReader.User
{
	public class ReadingProgress
	{
		public required string NovelName { get; set; }
		public required int ChapterNumber { get; set; }
		public required int ParagraphNumber { get; set; }

		public Dictionary<string, string> GetReadingProgressForNovel()
		{
			return new Dictionary<string, string> {
				{ "chapterNumber", ChapterNumber.ToString() },
				{ "paragraphNumber", ParagraphNumber.ToString() },
			};
		}
	}
}
