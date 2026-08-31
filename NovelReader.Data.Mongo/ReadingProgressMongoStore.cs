using MongoDB.Bson;
using MongoDB.Driver;
using NovelReader.Domain.RealTimeReader.User;

namespace NovelReader.Data.Mongo
{
	/// <summary>
	/// One document per (user, novel): <c>{ user, novel, chapter, paragraph, updatedAt }</c>.
	///
	/// The previous shape nested every novel inside a single per-user document, which made
	/// "which novels has this reader read?" a read-and-parse and a bookmark write a
	/// read-modify-write. Flat documents make the first a <c>distinct</c> on an indexed
	/// field and the second a one-document upsert (D18).
	/// </summary>
	internal sealed class ReadingProgressMongoStore(MongoClient mongoClient) : IReadingProgressStore
	{
		internal const string DatabaseName = "Users";
		internal const string CollectionName = "ReadingProgress";

		private IMongoCollection<BsonDocument> Collection =>
			mongoClient.GetDatabase(DatabaseName).GetCollection<BsonDocument>(CollectionName);

		private static FilterDefinition<BsonDocument> BuildFilter(string userName, string novelName)
		{
			return new BsonDocument { { "user", userName }, { "novel", novelName } };
		}

		/// <summary>
		/// Moves the bookmark. Sets only the bookmark's own fields — a replace would drop the
		/// cached catalogue details sharing this document, on every scroll (D22).
		/// </summary>
		public async Task SaveAsync(string userName, ReadingProgress progress, CancellationToken cancellationToken = default)
		{
			UpdateDefinition<BsonDocument> update = new BsonDocument("$set", new BsonDocument
			{
				{ "user", userName },
				{ "novel", progress.NovelName },
				{ "chapter", progress.ChapterNumber },
				{ "paragraph", progress.ParagraphNumber },
				{ "updatedAt", progress.UpdatedAtUtc }
			});

			await Collection.UpdateOneAsync(
				BuildFilter(userName, progress.NovelName),
				update,
				new UpdateOptions { IsUpsert = true },
				cancellationToken);
		}

		/// <summary>
		/// Writes the catalogue details, and always the timestamp. Like <see cref="SaveAsync"/>
		/// this sets only its own fields, so recording details cannot rewind a bookmark saved
		/// a moment earlier.
		/// </summary>
		public async Task SaveNovelDetailsAsync(
			string userName,
			string novelName,
			NovelSummary? summary,
			DateTime checkedAtUtc,
			CancellationToken cancellationToken = default)
		{
			BsonDocument fields = new()
			{
				{ "user", userName },
				{ "novel", novelName },
				{ "detailsCheckedAt", checkedAtUtc }
			};

			if (summary is not null)
			{
				if (summary.Title is not null) fields["title"] = summary.Title;
				if (summary.Rank is not null) fields["rank"] = summary.Rank.Value;
				if (summary.TotalChapters is not null) fields["totalChapters"] = summary.TotalChapters.Value;
			}

			await Collection.UpdateOneAsync(
				BuildFilter(userName, novelName),
				new BsonDocument("$set", fields),
				new UpdateOptions { IsUpsert = true },
				cancellationToken);
		}

		public async Task<ReadingProgress?> GetAsync(string userName, string novelName, CancellationToken cancellationToken = default)
		{
			BsonDocument? document = await Collection
				.Find(BuildFilter(userName, novelName))
				.FirstOrDefaultAsync(cancellationToken);

			return document is null ? null : ToProgress(document);
		}

		/// <summary>
		/// The novels this reader has open, newest first.
		///
		/// <c>distinct</c> answers "which novels?" straight from the (user, novel) index, but
		/// it returns them unordered. The recency order the menu wants comes from a second,
		/// projected read; the distinct list is what decides membership.
		/// </summary>
		public async Task<IReadOnlyList<NovelSummary>> GetNovelsReadAsync(string userName, CancellationToken cancellationToken = default)
		{
			FilterDefinition<BsonDocument> mine = new BsonDocument("user", userName);

			// `distinct` decides membership straight off the (user, novel) index; the read
			// below supplies the order and the details (D18).
			IAsyncCursor<string> cursor = await Collection.DistinctAsync<string>(
				"novel", mine, cancellationToken: cancellationToken);
			HashSet<string> known = new(await cursor.ToListAsync(cancellationToken), StringComparer.Ordinal);

			if (known.Count == 0)
			{
				return [];
			}

			List<BsonDocument> byRecency = await Collection
				.Find(mine)
				.Sort(new BsonDocument("updatedAt", -1))
				.ToListAsync(cancellationToken);

			List<NovelSummary> ordered = [];
			foreach (BsonDocument document in byRecency)
			{
				if (document.TryGetValue("novel", out BsonValue novel)
					&& novel.IsString
					&& known.Remove(novel.AsString))
				{
					ordered.Add(ToSummary(document, novel.AsString));
				}
			}

			// Anything distinct knew about but the ordered pass missed still belongs on the list.
			foreach (string missed in known)
			{
				ordered.Add(new NovelSummary { Slug = missed });
			}

			return ordered;
		}

		private static NovelSummary ToSummary(BsonDocument document, string slug)
		{
			return new NovelSummary
			{
				Slug = slug,
				Title = document.TryGetValue("title", out BsonValue title) && title.IsString
					? title.AsString
					: null,
				Rank = ReadNumber(document, "rank"),
				TotalChapters = ReadNumber(document, "totalChapters"),
				CheckedAtUtc = document.TryGetValue("detailsCheckedAt", out BsonValue checkedAt)
					&& checkedAt.IsValidDateTime
					? checkedAt.ToUniversalTime()
					: null
			};
		}

		public async Task<ReadingProgress?> GetMostRecentAsync(string userName, CancellationToken cancellationToken = default)
		{
			BsonDocument? document = await Collection
				.Find(new BsonDocument("user", userName))
				.Sort(new BsonDocument("updatedAt", -1))
				.FirstOrDefaultAsync(cancellationToken);

			return document is null ? null : ToProgress(document);
		}

		/// <summary>
		/// Tolerates documents written by the older shape, which stored the numbers as
		/// strings — a stored bookmark that cannot be parsed is no bookmark, not a crash.
		/// </summary>
		private static ReadingProgress? ToProgress(BsonDocument document)
		{
			if (!document.TryGetValue("novel", out BsonValue novel) || !novel.IsString)
			{
				return null;
			}

			return new ReadingProgress
			{
				NovelName = novel.AsString,
				ChapterNumber = ReadNumber(document, "chapter") ?? 1,
				ParagraphNumber = ReadNumber(document, "paragraph") ?? 1,
				UpdatedAtUtc = document.TryGetValue("updatedAt", out BsonValue updated) && updated.IsValidDateTime
					? updated.ToUniversalTime()
					: DateTime.UnixEpoch
			};
		}

		private static int? ReadNumber(BsonDocument document, string field)
		{
			if (!document.TryGetValue(field, out BsonValue value))
			{
				return null;
			}

			if (value.IsInt32) return value.AsInt32;
			if (value.IsInt64) return (int)value.AsInt64;
			if (value.IsString && int.TryParse(value.AsString, out int parsed)) return parsed;

			return null;
		}

		/// <summary>
		/// Rewrites <c>{ name, novels: { slug: { chapterNumber, paragraphNumber } } }</c> as one
		/// document per novel. The old writer stored the numbers as strings and only ever wrote
		/// them once, so the values are taken as-is and simply carried across.
		/// </summary>
		private static async Task<long> MigrateLegacyDocumentsAsync(
			IMongoCollection<BsonDocument> collection,
			CancellationToken cancellationToken)
		{
			// The new shape always has `novel`; anything without it predates this store.
			FilterDefinition<BsonDocument> legacy = new BsonDocument("novel", new BsonDocument("$exists", false));
			List<BsonDocument> documents = await collection.Find(legacy).ToListAsync(cancellationToken);

			long migrated = 0;
			foreach (BsonDocument document in documents)
			{
				if (!document.TryGetValue("name", out BsonValue name) || !name.IsString
					|| !document.TryGetValue("novels", out BsonValue novels) || !novels.IsBsonDocument)
				{
					// Not the old shape either; leave it rather than guess at it.
					continue;
				}

				foreach (BsonElement entry in novels.AsBsonDocument)
				{
					if (!entry.Value.IsBsonDocument)
					{
						continue;
					}

					BsonDocument progress = entry.Value.AsBsonDocument;
					BsonDocument replacement = new()
					{
						{ "user", name.AsString },
						{ "novel", entry.Name },
						{ "chapter", ReadNumber(progress, "chapterNumber") ?? 1 },
						{ "paragraph", ReadNumber(progress, "paragraphNumber") ?? 1 },
						{ "updatedAt", DateTime.UtcNow }
					};

					await collection.ReplaceOneAsync(
						BuildFilter(name.AsString, entry.Name),
						replacement,
						new ReplaceOptions { IsUpsert = true },
						cancellationToken);

					migrated++;
				}

				await collection.DeleteOneAsync(
					new BsonDocument("_id", document["_id"]),
					cancellationToken);
			}

			return migrated;
		}

		/// <summary>Unique per (user, novel), and the index that makes the sort and distinct cheap.</summary>
		internal static async Task EnsureIndexesAsync(MongoClient client, CancellationToken cancellationToken = default)
		{
			IMongoCollection<BsonDocument> collection = client
				.GetDatabase(DatabaseName)
				.GetCollection<BsonDocument>(CollectionName);

			// Old documents carry no `novel` field, so they would all collide on the unique
			// index. Spread them into the new shape before it exists.
			long migrated = await MigrateLegacyDocumentsAsync(collection, cancellationToken);
			if (migrated > 0)
			{
				Console.WriteLine($"Migrated {migrated} reading-progress entr(ies) to one document per novel.");
			}

			IndexKeysDefinitionBuilder<BsonDocument> keys = new();
			await collection.Indexes.CreateManyAsync(
			[
				new CreateIndexModel<BsonDocument>(
					keys.Ascending("user").Ascending("novel"),
					new CreateIndexOptions { Unique = true, Name = "user_novel_unique" }),
				new CreateIndexModel<BsonDocument>(
					keys.Ascending("user").Descending("updatedAt"),
					new CreateIndexOptions { Name = "user_recent" })
			], cancellationToken);
		}
	}
}
