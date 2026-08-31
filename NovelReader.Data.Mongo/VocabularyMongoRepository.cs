using MongoDB.Bson;
using MongoDB.Driver;
using NovelReader.Domain.RealTimeReader.Vocabulary;

namespace NovelReader.Data.Mongo
{
	/// <summary>
	/// One document per (user, term). Add and remove stay single-document operations, and a
	/// unique compound index on (user, term) makes "have I saved this?" a lookup rather
	/// than a scan (D3t).
	/// </summary>
	internal class VocabularyMongoRepository(MongoClient mongoClient) : IVocabularyRepository
	{
		internal const string DatabaseName = "Users";
		internal const string CollectionName = "Vocabulary";

		private IMongoCollection<BsonDocument> Collection =>
			mongoClient.GetDatabase(DatabaseName).GetCollection<BsonDocument>(CollectionName);

		public async Task AddAsync(string userName, VocabularyEntry entry, CancellationToken cancellationToken = default)
		{
			FilterDefinition<BsonDocument> filter = BuildFilter(userName, entry.NormalizedTerm);
			BsonDocument replacement = new()
			{
				{ "user", userName },
				{ "term", entry.NormalizedTerm },
				{ "surface", entry.SurfaceForm },
				{ "novel", entry.NovelName },
				{ "savedAt", entry.SavedAtUtc }
			};

			// Upsert so saving the same word twice is harmless rather than a duplicate-key error.
			await Collection.ReplaceOneAsync(
				filter,
				replacement,
				new ReplaceOptions { IsUpsert = true },
				cancellationToken);
		}

		public async Task RemoveAsync(string userName, string normalizedTerm, CancellationToken cancellationToken = default)
		{
			await Collection.DeleteOneAsync(BuildFilter(userName, normalizedTerm), cancellationToken);
		}

		public async Task<bool> ContainsAsync(string userName, string normalizedTerm, CancellationToken cancellationToken = default)
		{
			long count = await Collection.CountDocumentsAsync(
				BuildFilter(userName, normalizedTerm),
				new CountOptions { Limit = 1 },
				cancellationToken);

			return count > 0;
		}

		public async Task<IReadOnlyCollection<VocabularyEntry>> GetAllForUserAsync(string userName, CancellationToken cancellationToken = default)
		{
			FilterDefinitionBuilder<BsonDocument> builder = new();
			FilterDefinition<BsonDocument> filter = builder.Eq(document => document["user"], userName);

			List<VocabularyEntry> entries = [];
			using IAsyncCursor<BsonDocument> cursor = await Collection.FindAsync(filter, cancellationToken: cancellationToken);
			while (await cursor.MoveNextAsync(cancellationToken))
			{
				foreach (BsonDocument document in cursor.Current)
				{
					entries.Add(ToEntry(document));
				}
			}

			return entries;
		}

		private static VocabularyEntry ToEntry(BsonDocument document)
		{
			return new VocabularyEntry
			{
				NormalizedTerm = document.GetValue("term", BsonString.Empty).AsString,
				SurfaceForm = document.GetValue("surface", BsonString.Empty).AsString,
				NovelName = document.GetValue("novel", BsonString.Empty).AsString,
				SavedAtUtc = document.TryGetValue("savedAt", out BsonValue savedAt) && savedAt.IsValidDateTime
					? savedAt.ToUniversalTime()
					: DateTime.UnixEpoch
			};
		}

		private static FilterDefinition<BsonDocument> BuildFilter(string userName, string normalizedTerm)
		{
			FilterDefinitionBuilder<BsonDocument> builder = new();
			return builder.And(
				builder.Eq(document => document["user"], userName),
				builder.Eq(document => document["term"], normalizedTerm));
		}

		/// <summary>Idempotent; safe to call on every start.</summary>
		internal static async Task EnsureIndexesAsync(MongoClient client, CancellationToken cancellationToken = default)
		{
			IMongoCollection<BsonDocument> collection = client
				.GetDatabase(DatabaseName)
				.GetCollection<BsonDocument>(CollectionName);

			IndexKeysDefinitionBuilder<BsonDocument> keys = new();
			CreateIndexModel<BsonDocument> index = new(
				keys.Ascending("user").Ascending("term"),
				new CreateIndexOptions { Unique = true, Name = "user_term_unique" });

			await collection.Indexes.CreateOneAsync(index, cancellationToken: cancellationToken);
		}
	}
}
