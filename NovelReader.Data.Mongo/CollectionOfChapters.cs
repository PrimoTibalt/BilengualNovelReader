using MongoDB.Driver;
using NovelReader.Domain;

namespace NovelReader.Data.Mongo
{
	internal class CollectionOfChapters : ICollectionOfChapters
	{
		internal required IMongoCollection<Chapter> Value { get; init; }

		public async Task<IFilteredCollection> FilterByChapter(int chapterNumber)
		{
			return new FilteredCollection
			{
				Value = await Value.FindAsync(chapter => chapter.ChapterNumber == chapterNumber)
			};
		}

		public async Task<IChapter> InsertOneAsync(int chapterNumber, Lazy<Task<Dictionary<int, string>>> paragraphsLazy)
		{
			Dictionary<int, string> paragraphs = await paragraphsLazy.Value;
			Chapter chapterDocument = new(chapterNumber, paragraphs.Select(pair => new KeyValuePair<string, string>(pair.Key.ToString(), pair.Value)).ToDictionary());
			await Value.InsertOneAsync(chapterDocument);
			return chapterDocument;
		}
	}
}
