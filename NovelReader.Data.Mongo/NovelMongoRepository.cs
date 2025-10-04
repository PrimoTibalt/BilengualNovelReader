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

			return new CollectionOfChapters
			{
				Value = novelsDatabase.GetCollection<Chapter>(novelName)
			};
		}
	}
}
