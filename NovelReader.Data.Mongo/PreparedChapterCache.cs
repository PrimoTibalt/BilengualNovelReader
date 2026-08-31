using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using MongoDB.Bson;
using MongoDB.Driver;
using NovelReader.Domain.RealTimeReader.Reading;

namespace NovelReader.Data.Mongo
{
	/// <summary>
	/// Two-tier prepared-chapter cache (D8). MongoDB holds the chapter the reader is about
	/// to reach, keyed exactly as specified: {user}/{novel}/chapter{N}. Once they start on
	/// it, it is promoted into memory and MongoDB moves on to the chapter after that.
	/// </summary>
	internal class PreparedChapterCache(MongoClient mongoClient, IMemoryCache memoryCache) : IPreparedChapterCache
	{
		internal const string DatabaseName = "Cache";
		internal const string CollectionName = "PreparedChapters";

		private static readonly TimeSpan MemoryTierLifetime = TimeSpan.FromMinutes(30);

		/// <summary>
		/// IMemoryCache cannot enumerate its keys, so each user gets a token that is
		/// cancelled to evict everything of theirs at once when their vocabulary changes.
		/// </summary>
		private static readonly ConcurrentDictionary<string, CancellationTokenSource> UserEvictionTokens = new();

		private IMongoCollection<BsonDocument> Collection =>
			mongoClient.GetDatabase(DatabaseName).GetCollection<BsonDocument>(CollectionName);

		internal static string BuildKey(string userName, string novelName, int chapterNumber) =>
			$"{userName}/{novelName}/chapter{chapterNumber}";

		public Task<PreparedChapter?> TryGetFromMemoryAsync(
			string userName,
			string novelName,
			int chapterNumber,
			CancellationToken cancellationToken = default)
		{
			string key = BuildKey(userName, novelName, chapterNumber);
			memoryCache.TryGetValue(key, out PreparedChapter? fromMemory);
			return Task.FromResult(fromMemory);
		}

		public async Task<PreparedChapter?> TryGetFromDurableAsync(
			string userName,
			string novelName,
			int chapterNumber,
			CancellationToken cancellationToken = default)
		{
			return await ReadFromDurableTierAsync(BuildKey(userName, novelName, chapterNumber), cancellationToken);
		}

		public async Task StoreInDurableAsync(string userName, PreparedChapter chapter, CancellationToken cancellationToken = default)
		{
			string key = BuildKey(userName, chapter.NovelName, chapter.ChapterNumber);

			BsonDocument paragraphs = [];
			foreach ((int paragraphNumber, string markup) in chapter.Paragraphs)
			{
				paragraphs.Add(paragraphNumber.ToString(), markup);
			}

			BsonDocument replacement = new()
			{
				{ "_id", key },
				{ "user", userName },
				{ "novel", chapter.NovelName },
				{ "chapter", chapter.ChapterNumber },
				{ "paragraphs", paragraphs },
				{ "preparedAt", chapter.PreparedAtUtc }
			};

			FilterDefinitionBuilder<BsonDocument> builder = new();
			await Collection.ReplaceOneAsync(
				builder.Eq(document => document["_id"], key),
				replacement,
				new ReplaceOptions { IsUpsert = true },
				cancellationToken);
		}

		public async Task PromoteToMemoryAsync(string userName, PreparedChapter chapter, CancellationToken cancellationToken = default)
		{
			string key = BuildKey(userName, chapter.NovelName, chapter.ChapterNumber);

			CancellationTokenSource evictionToken = UserEvictionTokens.GetOrAdd(userName, _ => new CancellationTokenSource());
			MemoryCacheEntryOptions options = new()
			{
				Size = 1,
				SlidingExpiration = MemoryTierLifetime
			};
			options.AddExpirationToken(new Microsoft.Extensions.Primitives.CancellationChangeToken(evictionToken.Token));
			memoryCache.Set(key, chapter, options);

			// The durable tier exists to hold the chapter the reader has not reached yet, so
			// once this one is hot in memory it gives up its slot.
			FilterDefinitionBuilder<BsonDocument> builder = new();
			await Collection.DeleteOneAsync(builder.Eq(document => document["_id"], key), cancellationToken);
		}

		public async Task InvalidateForUserAsync(string userName, CancellationToken cancellationToken = default)
		{
			if (UserEvictionTokens.TryRemove(userName, out CancellationTokenSource? evictionToken))
			{
				await evictionToken.CancelAsync();
				evictionToken.Dispose();
			}

			FilterDefinitionBuilder<BsonDocument> builder = new();
			await Collection.DeleteManyAsync(
				builder.Eq(document => document["user"], userName),
				cancellationToken);
		}

		private async Task<PreparedChapter?> ReadFromDurableTierAsync(string key, CancellationToken cancellationToken)
		{
			FilterDefinitionBuilder<BsonDocument> builder = new();
			using IAsyncCursor<BsonDocument> cursor = await Collection.FindAsync(
				builder.Eq(document => document["_id"], key),
				cancellationToken: cancellationToken);

			if (!await cursor.MoveNextAsync(cancellationToken) || !cursor.Current.Any())
			{
				return null;
			}

			return ToPreparedChapter(cursor.Current.First());
		}

		private static PreparedChapter? ToPreparedChapter(BsonDocument document)
		{
			if (!document.TryGetValue("paragraphs", out BsonValue paragraphsValue) || !paragraphsValue.IsBsonDocument)
			{
				return null;
			}

			Dictionary<int, string> paragraphs = [];
			foreach (BsonElement element in paragraphsValue.AsBsonDocument)
			{
				if (int.TryParse(element.Name, out int paragraphNumber))
				{
					paragraphs[paragraphNumber] = element.Value.AsString;
				}
			}

			return new PreparedChapter
			{
				NovelName = document.GetValue("novel", BsonString.Empty).AsString,
				ChapterNumber = document.TryGetValue("chapter", out BsonValue chapter) && chapter.IsNumeric
					? chapter.ToInt32()
					: 0,
				Paragraphs = paragraphs,
				PreparedAtUtc = document.TryGetValue("preparedAt", out BsonValue preparedAt) && preparedAt.IsValidDateTime
					? preparedAt.ToUniversalTime()
					: DateTime.UnixEpoch
			};
		}
	}
}
