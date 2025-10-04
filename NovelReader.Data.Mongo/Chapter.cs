using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using NovelReader.Domain;

namespace NovelReader.Data.Mongo
{
	internal class Chapter : IChapter
	{
		[BsonId]
		public ObjectId Id { get; set; }

		[BsonElement("chapter")]
		public int ChapterNumber { get; init; }

		[BsonExtraElements]
		public Dictionary<string, object> Paragraphs { get; init; }

		public Chapter(int chapterNumber, Dictionary<string, string> keyValuePairs)
		{
			ChapterNumber = chapterNumber;
			Paragraphs = keyValuePairs.Select(pair => new KeyValuePair<string, object>(pair.Key, pair.Value))
				.ToDictionary();
		}

		public bool TryGetParagraph(int paragraphNumber, out string paragraph)
		{
			if (Paragraphs.TryGetValue($"{paragraphNumber}", out object? paragraphObject))
			{
				paragraph = (string)paragraphObject;
				return true;
			}

			paragraph = string.Empty;
			return false;
		}
	}
}
