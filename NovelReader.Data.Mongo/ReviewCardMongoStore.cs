using MongoDB.Bson;
using MongoDB.Driver;
using NovelReader.Domain.RealTimeReader.Review;

namespace NovelReader.Data.Mongo
{
	/// <summary>
	/// One document per (user, term) — the same shape as the vocabulary beside it, so a card is
	/// written and removed as one document and a reader's whole deck is one indexed query.
	/// </summary>
	internal sealed class ReviewCardMongoStore(MongoClient mongoClient) : IReviewCardStore
	{
		internal const string DatabaseName = "Users";
		internal const string CollectionName = "ReviewCards";

		private IMongoCollection<BsonDocument> Collection =>
			mongoClient.GetDatabase(DatabaseName).GetCollection<BsonDocument>(CollectionName);

		private static FilterDefinition<BsonDocument> BuildFilter(string userName, string term) =>
			new BsonDocument { { "user", userName }, { "term", term } };

		public async Task<IReadOnlyList<ReviewCard>> GetAllAsync(string userName, CancellationToken cancellationToken = default)
		{
			List<BsonDocument> documents = await Collection
				.Find(new BsonDocument("user", userName))
				.ToListAsync(cancellationToken);

			return [.. documents.Select(ToCard)];
		}

		public async Task SaveAsync(string userName, ReviewCard card, CancellationToken cancellationToken = default)
		{
			BsonDocument replacement = new()
			{
				{ "user", userName },
				{ "term", card.Term },
				{ "state", card.State.ToString() },
				{ "step", card.Step },
				{ "ease", card.Ease },
				{ "interval", card.IntervalDays },
				{ "due", card.DueUtc },
				{ "reviews", card.Reviews },
				{ "lapses", card.Lapses },
				{ "firstReviewedAt", card.FirstReviewedUtc is { } first ? first : BsonNull.Value },
				{ "lastReviewedAt", card.LastReviewedUtc is { } last ? last : BsonNull.Value },
				{ "gloss", card.Gloss is null ? BsonNull.Value : card.Gloss }
			};

			await Collection.ReplaceOneAsync(
				BuildFilter(userName, card.Term),
				replacement,
				new ReplaceOptions { IsUpsert = true },
				cancellationToken);
		}

		public async Task RemoveAsync(string userName, string term, CancellationToken cancellationToken = default)
		{
			await Collection.DeleteOneAsync(BuildFilter(userName, term), cancellationToken);
		}

		private static ReviewCard ToCard(BsonDocument document) => new()
		{
			Term = document.GetValue("term", BsonString.Empty).AsString,
			State = Enum.TryParse(document.GetValue("state", "New").AsString, out CardState state) ? state : CardState.New,
			Step = document.GetValue("step", 0).ToInt32(),
			Ease = document.GetValue("ease", ReviewScheduler.StartingEase).ToDouble(),
			IntervalDays = document.GetValue("interval", 0.0).ToDouble(),
			DueUtc = ReadDate(document, "due") ?? DateTime.UnixEpoch,
			Reviews = document.GetValue("reviews", 0).ToInt32(),
			Lapses = document.GetValue("lapses", 0).ToInt32(),
			FirstReviewedUtc = ReadDate(document, "firstReviewedAt"),
			LastReviewedUtc = ReadDate(document, "lastReviewedAt"),
			Gloss = document.TryGetValue("gloss", out BsonValue gloss) && gloss.IsString ? gloss.AsString : null
		};

		private static DateTime? ReadDate(BsonDocument document, string name) =>
			document.TryGetValue(name, out BsonValue value) && value.IsValidDateTime ? value.ToUniversalTime() : null;

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
