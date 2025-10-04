namespace NovelReader.Domain
{
	public interface ICollectionOfChapters
	{
		Task<IFilteredCollection> FilterByChapter(int chapterNumber);
		Task<IChapter> InsertOneAsync(int chapterNumber, Lazy<Task<Dictionary<int, string>>> paragraphsLazy);
	}
}
