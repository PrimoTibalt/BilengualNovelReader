using MongoDB.Driver;
using NovelReader.Domain;

namespace NovelReader.Data.Mongo
{
	internal class NovelMongoRepository(MongoClient mongoClient) : INovelRepository
	{
		public async Task<ICollectionOfChapters> GetCollectionOfChapters(string novelName)
		{
			IMongoDatabase novelsDatabase = mongoClient.GetDatabase("Novels");
			IAsyncCursor<string> listOfNovelsCursor = await novelsDatabase.ListCollectionNamesAsync();
			bool exists = false;
			while (await listOfNovelsCursor.MoveNextAsync())
			{
				if (listOfNovelsCursor.Current.Contains(novelName))
				{
					exists = true;
					break;
				}
			}

			if (!exists)
				await novelsDatabase.CreateCollectionAsync(novelName);

			// One document per chapter, enforced by the database (D17). Runs once per novel
			// per process, and cleans up any duplicates an earlier run already stored.
			try
			{
				await ChapterIndexes.EnsureAsync(novelsDatabase, novelName);
			}
			catch (Exception exception)
			{
				// Reading worked before this index existed and must keep working without it.
				Console.WriteLine($"Could not ensure the chapter index for '{novelName}': {exception.Message}");
			}

			return new CollectionOfChapters
			{
				Value = novelsDatabase.GetCollection<Chapter>(novelName)
			};
		}
	}
}
