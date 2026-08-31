using MongoDB.Bson;
using MongoDB.Driver;
using NovelReader.Domain.RealTimeReader.Definitions;

namespace NovelReader.Data.Mongo
{
	/// <summary>
	/// Global definition cache (D2), keyed by normalised term. Positive entries never
	/// expire — a dictionary definition does not go stale — while negative entries do, so a
	/// word the providers were merely having a bad day about gets another chance.
	/// </summary>
	internal class DefinitionMongoCache(MongoClient mongoClient) : IDefinitionCache
	{
		internal const string DatabaseName = "Dictionary";
		internal const string CollectionName = "Definitions";

		private static readonly TimeSpan NegativeCacheLifetime = TimeSpan.FromDays(7);

		private IMongoCollection<BsonDocument> Collection =>
			mongoClient.GetDatabase(DatabaseName).GetCollection<BsonDocument>(CollectionName);

		public async Task<DefinitionLookupResult> TryGetAsync(string normalizedTerm, CancellationToken cancellationToken = default)
		{
			FilterDefinitionBuilder<BsonDocument> builder = new();
			FilterDefinition<BsonDocument> filter = builder.Eq(document => document["_id"], normalizedTerm);

			using IAsyncCursor<BsonDocument> cursor = await Collection.FindAsync(filter, cancellationToken: cancellationToken);
			if (!await cursor.MoveNextAsync(cancellationToken) || !cursor.Current.Any())
			{
				return DefinitionLookupResult.Unknown;
			}

			BsonDocument document = cursor.Current.First();

			if (document.GetValue("missing", BsonBoolean.False).ToBoolean())
			{
				DateTime recordedAt = document.TryGetValue("fetchedAt", out BsonValue fetchedAt) && fetchedAt.IsValidDateTime
					? fetchedAt.ToUniversalTime()
					: DateTime.UnixEpoch;

				bool stillTrusted = DateTime.UtcNow - recordedAt < NegativeCacheLifetime;
				return stillTrusted ? DefinitionLookupResult.Missing : DefinitionLookupResult.Unknown;
			}

			WordDefinition? definition = ToDefinition(normalizedTerm, document);
			return definition is null ? DefinitionLookupResult.Unknown : DefinitionLookupResult.Hit(definition);
		}

		public async Task StoreAsync(WordDefinition definition, CancellationToken cancellationToken = default)
		{
			BsonArray senses = [];
			foreach (DefinitionSense sense in definition.Senses)
			{
				BsonDocument senseDocument = new() { { "text", sense.Text } };
				if (sense.PartOfSpeech is not null)
				{
					senseDocument.Add("partOfSpeech", sense.PartOfSpeech);
				}

				if (sense.Example is not null)
				{
					senseDocument.Add("example", sense.Example);
				}

				senses.Add(senseDocument);
			}

			BsonDocument replacement = new()
			{
				{ "_id", definition.Term },
				{ "missing", false },
				{ "source", definition.SourceName },
				{ "senses", senses },
				{ "fetchedAt", DateTime.UtcNow }
			};

			if (definition.SourceUrl is not null)
			{
				replacement.Add("sourceUrl", definition.SourceUrl);
			}

			await ReplaceAsync(definition.Term, replacement, cancellationToken);
		}

		public async Task StoreMissAsync(string normalizedTerm, CancellationToken cancellationToken = default)
		{
			BsonDocument replacement = new()
			{
				{ "_id", normalizedTerm },
				{ "missing", true },
				{ "fetchedAt", DateTime.UtcNow }
			};

			await ReplaceAsync(normalizedTerm, replacement, cancellationToken);
		}

		private async Task ReplaceAsync(string term, BsonDocument replacement, CancellationToken cancellationToken)
		{
			FilterDefinitionBuilder<BsonDocument> builder = new();
			await Collection.ReplaceOneAsync(
				builder.Eq(document => document["_id"], term),
				replacement,
				new ReplaceOptions { IsUpsert = true },
				cancellationToken);
		}

		private static WordDefinition? ToDefinition(string term, BsonDocument document)
		{
			if (!document.TryGetValue("senses", out BsonValue sensesValue) || !sensesValue.IsBsonArray)
			{
				return null;
			}

			List<DefinitionSense> senses = [];
			foreach (BsonValue senseValue in sensesValue.AsBsonArray)
			{
				if (!senseValue.IsBsonDocument)
				{
					continue;
				}

				BsonDocument senseDocument = senseValue.AsBsonDocument;
				string text = senseDocument.GetValue("text", BsonString.Empty).AsString;
				if (text.Length == 0)
				{
					continue;
				}

				senses.Add(new DefinitionSense
				{
					Text = text,
					PartOfSpeech = senseDocument.TryGetValue("partOfSpeech", out BsonValue partOfSpeech) ? partOfSpeech.AsString : null,
					Example = senseDocument.TryGetValue("example", out BsonValue example) ? example.AsString : null
				});
			}

			if (senses.Count == 0)
			{
				return null;
			}

			return new WordDefinition
			{
				Term = term,
				Senses = senses,
				SourceName = document.GetValue("source", BsonString.Empty).AsString,
				SourceUrl = document.TryGetValue("sourceUrl", out BsonValue sourceUrl) ? sourceUrl.AsString : null
			};
		}
	}
}
