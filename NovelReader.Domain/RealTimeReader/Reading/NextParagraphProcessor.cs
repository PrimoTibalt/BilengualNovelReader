using NovelReader.Domain.RealTimeReader.Parsing;

namespace NovelReader.Domain.RealTimeReader.Reading
{
	public class NextParagraphProcessor(
		INovelRepository novelRepository,
		IParagraphsRetriever paragraphsRetriever)
	{
		public async Task<ParagraphData> ProcessAndReturnAsync(string name, int chapterNumber, int paragraphNumber)
		{
			ICollectionOfChapters chaptersCollection = await novelRepository.GetCollectionOfChapters(name);
			IFilteredCollection chapterDocumentCursor = await chaptersCollection.FilterByChapter(chapterNumber);
			Lazy<Task<Dictionary<int, string>>> paragraphsLazy = new(() => paragraphsRetriever.GetParagraphsAsync($"book/{name}/chapter-{chapterNumber}"));
			IChapter chapterDocument = await GetOrCreateChapterDocument(chapterDocumentCursor, chaptersCollection, paragraphsLazy, chapterNumber);
			if (chapterDocument.TryGetParagraph(paragraphNumber, out string paragraph))
			{
				return new ParagraphData()
				{
					BookName = name,
					ChapterNumber = chapterNumber,
					ParagraphNumber = paragraphNumber,
					Content = paragraph
				};
			}
			else
			{
				return await ProcessAndReturnAsync(name, chapterNumber + 1, 1);
			}
		}

		private static async Task<IChapter> GetOrCreateChapterDocument(
			IFilteredCollection filteredChapterDocumentCollection,
			ICollectionOfChapters chaptersCollection,
			Lazy<Task<Dictionary<int, string>>> paragraphsLazy,
			int chapterNumber)
		{
			IChapter? chapter = await filteredChapterDocumentCollection.TryGetExactlyOne();
			if (chapter is not null)
			{
				return chapter;
			}
			else
			{
				return await chaptersCollection.InsertOneAsync(chapterNumber, paragraphsLazy);
			}
		}
	}
}
