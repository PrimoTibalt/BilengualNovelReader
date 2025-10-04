using MongoDB.Bson;
using MongoDB.Driver;
using NovelReader.Domain.RealTimeReader.User;

namespace NovelReader.Data.Mongo
{
	internal class UserDataHandler(MongoClient mongoClient) : IUserDataHandler
	{
		public async Task UpdateReadingProgressAsync(string userName, ReadingProgress readingProgress)
		{
			IMongoDatabase usersDatabase = mongoClient.GetDatabase("Users");
			IMongoCollection<BsonDocument> usersCollection = usersDatabase.GetCollection<BsonDocument>("ReadingProgress");
			FilterDefinitionBuilder<BsonDocument> findUserFilterBuilder = new();
			FilterDefinition<BsonDocument> findUserFilter = findUserFilterBuilder.Eq(userDocument => userDocument["name"], userName);
			UpdateDefinitionBuilder<BsonDocument> updateFilterBuilder = new();
			UpdateDefinition<BsonDocument> userReadingProgressUpdate = updateFilterBuilder.SetOnInsert(
				userDocument => userDocument["novels"][readingProgress.NovelName],
				new BsonDocument(readingProgress.GetReadingProgressForNovel())
				);
			FindOneAndUpdateOptions<BsonDocument, BsonDocument> options = new();
			options.IsUpsert = true;
			await usersCollection.FindOneAndUpdateAsync(findUserFilter, userReadingProgressUpdate, options);
		}

		public async Task<ReadingProgress> GetOrCreateUserProgressOnNovelAsync(string userName, string novelName)
		{
			IMongoDatabase usersDatabase = mongoClient.GetDatabase("Users");
			IMongoCollection<BsonDocument> usersCollection = usersDatabase.GetCollection<BsonDocument>("ReadingProgress");
			FilterDefinitionBuilder<BsonDocument> findUserFilterBuilder = new();
			FilterDefinition<BsonDocument> findUserFilter = findUserFilterBuilder.Eq(userDocument => userDocument["name"], userName);
			IAsyncCursor<BsonDocument> userDocumentsCursor = await usersCollection.FindAsync(findUserFilter);
			BsonDocument userDocument;
			if (await userDocumentsCursor.MoveNextAsync() && userDocumentsCursor.Current.Any())
			{
				userDocument = userDocumentsCursor.Current.Single();
			}
			else
			{
				Dictionary<string, BsonValue> initialData = new()
				{
					{ "name", userName },
					{ "novels", new BsonDocument() },
				};
				userDocument = new BsonDocument(initialData);
				await usersCollection.InsertOneAsync(userDocument);
			}

			if (userDocument["novels"].AsBsonDocument.TryGetValue(novelName, out BsonValue novelProgress))
			{
				BsonDocument novelProgressDocument = novelProgress.AsBsonDocument;
				return new ReadingProgress
				{
					NovelName = novelName,
					ChapterNumber = novelProgressDocument["chapterNumber"].AsInt32,
					ParagraphNumber = novelProgressDocument["paragraphNumber"].AsInt32
				};
			}
			else
			{
				BsonDocument novelsReadDocument = userDocument["novels"].AsBsonDocument;
				const int chapterNumber = 1;
				const int paragraphNumber = 1;
				ReadingProgress startNovelProgress = new()
				{
					NovelName = novelName,
					ChapterNumber = chapterNumber,
					ParagraphNumber = paragraphNumber,
				};
				novelsReadDocument.AddRange(startNovelProgress.GetReadingProgressForNovel());
				UpdateDefinitionBuilder<BsonDocument> updateDefinitionBuilder = new();
				UpdateDefinition<BsonDocument> updateDefinition = updateDefinitionBuilder.Set(userDoc => userDoc["novels"], novelsReadDocument);
				await usersCollection.UpdateOneAsync(findUserFilter, updateDefinition);
				return startNovelProgress;
			}
		}
	}
}
