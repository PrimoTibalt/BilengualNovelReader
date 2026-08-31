using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;

namespace NovelReader.Data.Mongo
{
	/// <summary>
	/// Makes "one document per chapter" a rule the database enforces, rather than a habit the
	/// callers are trusted to keep (D17).
	///
	/// Each novel is its own collection, created on demand, so the index cannot be built once
	/// at startup the way the vocabulary index is — it is ensured the first time a novel's
	/// collection is asked for, once per novel per process.
	/// </summary>
	internal static class ChapterIndexes
	{
		private const string IndexName = "chapter_unique";

		/// <summary>
		/// Novels already handled in this process. The task is cached whatever the outcome:
		/// one attempt per novel per process, so a database that refuses the index does not
		/// make every read pay for a retry.
		/// </summary>
		private static readonly ConcurrentDictionary<string, Task> ensured = new(StringComparer.Ordinal);

		internal static Task EnsureAsync(IMongoDatabase database, string novelName)
		{
			return ensured.GetOrAdd(novelName, _ => EnsureCoreAsync(database, novelName));
		}

		private static async Task EnsureCoreAsync(IMongoDatabase database, string novelName)
		{
			IMongoCollection<BsonDocument> collection = database.GetCollection<BsonDocument>(novelName);

			try
			{
				await CreateIndexAsync(collection);
			}
			catch (MongoCommandException)
			{
				// The collection already holds duplicates, so the unique index is refused.
				// Clear them out and try once more.
				long removed = await RemoveDuplicateChaptersAsync(collection);
				Console.WriteLine($"Removed {removed} duplicate chapter document(s) from '{novelName}'.");

				await CreateIndexAsync(collection);
			}
		}

		private static Task CreateIndexAsync(IMongoCollection<BsonDocument> collection)
		{
			IndexKeysDefinitionBuilder<BsonDocument> keys = new();
			CreateIndexModel<BsonDocument> index = new(
				keys.Ascending("chapter"),
				new CreateIndexOptions { Unique = true, Name = IndexName });

			return collection.Indexes.CreateOneAsync(index);
		}

		/// <summary>
		/// Keeps one document per chapter number and deletes the rest. Every copy is the same
		/// scrape, so the one with the most fields is kept — that is the most complete of them
		/// if a writer was ever interrupted part-way.
		/// </summary>
		private static async Task<long> RemoveDuplicateChaptersAsync(IMongoCollection<BsonDocument> collection)
		{
			BsonDocument group = new()
			{
				{ "_id", "$chapter" },
				{ "ids", new BsonDocument("$push", "$_id") },
				{ "count", new BsonDocument("$sum", 1) }
			};

			BsonDocument[] stages =
			[
				new BsonDocument("$group", group),
				new BsonDocument("$match", new BsonDocument("count", new BsonDocument("$gt", 1)))
			];

			PipelineDefinition<BsonDocument, BsonDocument> pipeline = stages;
			List<BsonDocument> duplicated = await (await collection.AggregateAsync(pipeline)).ToListAsync();

			long removed = 0;
			foreach (BsonDocument duplicate in duplicated)
			{
				List<BsonValue> ids = [.. duplicate["ids"].AsBsonArray];
				BsonValue keep = await ChooseMostCompleteAsync(collection, ids);

				DeleteResult result = await collection.DeleteManyAsync(new BsonDocument
				{
					{ "_id", new BsonDocument("$in", new BsonArray(ids.Where(id => id != keep))) }
				});

				removed += result.DeletedCount;
			}

			return removed;
		}

		private static async Task<BsonValue> ChooseMostCompleteAsync(
			IMongoCollection<BsonDocument> collection,
			List<BsonValue> ids)
		{
			List<BsonDocument> documents = await collection
				.Find(new BsonDocument { { "_id", new BsonDocument("$in", new BsonArray(ids)) } })
				.ToListAsync();

			BsonDocument? best = null;
			foreach (BsonDocument document in documents)
			{
				if (best is null || document.ElementCount > best.ElementCount)
				{
					best = document;
				}
			}

			return best?["_id"] ?? ids[0];
		}
	}
}
