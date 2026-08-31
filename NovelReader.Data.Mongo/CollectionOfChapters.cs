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

		/// <summary>
		/// Stores a chapter, tolerating the case where another writer stored it first. The
		/// unique index on <c>chapter</c> turns that race into a duplicate-key error instead
		/// of a second copy (D17), and the answer to it is simply to use what they wrote.
		/// </summary>
		public async Task<IChapter> InsertOneAsync(int chapterNumber, Lazy<Task<Dictionary<int, string>>> paragraphsLazy)
		{
			Dictionary<int, string> paragraphs = await paragraphsLazy.Value;
			Chapter chapterDocument = new(chapterNumber, paragraphs.Select(pair => new KeyValuePair<string, string>(pair.Key.ToString(), pair.Value)).ToDictionary());

			try
			{
				await Value.InsertOneAsync(chapterDocument);
				return chapterDocument;
			}
			catch (MongoWriteException exception)
				when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
			{
				IChapter? stored = await (await FilterByChapter(chapterNumber)).TryGetExactlyOne();

				// Their copy is the same scrape as ours; falling back to ours only matters if
				// it was deleted in between.
				return stored ?? chapterDocument;
			}
		}
	}
}
