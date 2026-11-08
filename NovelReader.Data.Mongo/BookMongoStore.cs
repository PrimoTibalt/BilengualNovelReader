using MongoDB.Bson;
using MongoDB.Driver;
using NovelReader.Domain.RealTimeReader.Books;

namespace NovelReader.Data.Mongo
{
	/// <summary>
	/// Uploaded books (D50), in their own database <c>Books</c>:
	/// <list type="bullet">
	/// <item><c>Books</c>, one document per book: <c>{ _id: slug, owner, title, author, language,
	/// format, chapters: [titles], uploadedAt }</c>, indexed by owner.</item>
	/// <item><c>Chapters</c>, one document per chapter: <c>{ _id: "slug/N", book, owner, chapter,
	/// title, paragraphs }</c>. A paragraph is a string when it is only text — nearly all of them —
	/// and otherwise an array of strings and <c>{ img, w, h, alt }</c> pictures, in order.</item>
	/// </list>
	/// Every chapter carries its owner, so reading one is a single lookup on <c>_id</c> that also
	/// answers "is it theirs?". Unlike the scraped novels' one collection per novel, every book
	/// shares these two: a book is private to one reader, so there could be thousands.
	/// </summary>
	internal sealed class BookMongoStore(MongoClient mongoClient) : IBookStore
	{
		internal const string DatabaseName = "Books";
		internal const string BooksCollectionName = "Books";
		internal const string ChaptersCollectionName = "Chapters";

		private IMongoCollection<BsonDocument> Books =>
			mongoClient.GetDatabase(DatabaseName).GetCollection<BsonDocument>(BooksCollectionName);

		private IMongoCollection<BsonDocument> Chapters =>
			mongoClient.GetDatabase(DatabaseName).GetCollection<BsonDocument>(ChaptersCollectionName);

		private static string ChapterId(string slug, int chapterNumber) => $"{slug}/{chapterNumber}";

		public async Task SaveAsync(StoredBook book, IReadOnlyList<BookChapter> chapters, CancellationToken cancellationToken = default)
		{
			List<BsonDocument> chapterDocuments = [.. chapters.Select(chapter => ToDocument(book, chapter))];
			if (chapterDocuments.Count > 0)
			{
				await Chapters.InsertManyAsync(chapterDocuments, cancellationToken: cancellationToken);
			}

			// Last: a book that is listed has all its chapters.
			await Books.InsertOneAsync(new BsonDocument
			{
				{ "_id", book.Slug },
				{ "owner", book.Owner },
				{ "title", book.Title },
				{ "author", book.Author is null ? BsonNull.Value : book.Author },
				{ "language", book.Language is null ? BsonNull.Value : book.Language },
				{ "format", book.Format },
				{ "chapters", new BsonArray(book.ChapterTitles) },
				{ "uploadedAt", book.UploadedAtUtc },
			}, cancellationToken: cancellationToken);
		}

		public async Task<StoredBook?> GetAsync(string owner, string slug, CancellationToken cancellationToken = default)
		{
			BsonDocument? document = await Books
				.Find(new BsonDocument { { "_id", slug }, { "owner", owner } })
				.FirstOrDefaultAsync(cancellationToken);

			return document is null ? null : ToBook(document);
		}

		public async Task<BookChapter?> GetChapterAsync(string owner, string slug, int chapterNumber, CancellationToken cancellationToken = default)
		{
			BsonDocument? document = await Chapters
				.Find(new BsonDocument { { "_id", ChapterId(slug, chapterNumber) }, { "owner", owner } })
				.FirstOrDefaultAsync(cancellationToken);

			return document is null ? null : ToChapter(document);
		}

		public Task<long> CountForOwnerAsync(string owner, CancellationToken cancellationToken = default) =>
			Books.CountDocumentsAsync(new BsonDocument("owner", owner), cancellationToken: cancellationToken);

		public async Task DeleteAsync(string owner, string slug, CancellationToken cancellationToken = default)
		{
			await Books.DeleteOneAsync(new BsonDocument { { "_id", slug }, { "owner", owner } }, cancellationToken);
			await Chapters.DeleteManyAsync(new BsonDocument { { "book", slug }, { "owner", owner } }, cancellationToken);
		}

		// ---- Shapes ----

		private static BsonDocument ToDocument(StoredBook book, BookChapter chapter)
		{
			BsonArray paragraphs = [];
			foreach (BookParagraph paragraph in chapter.Paragraphs)
			{
				if (paragraph.Inlines.All(inline => inline.Image is null))
				{
					paragraphs.Add(paragraph.PlainText);
					continue;
				}

				BsonArray parts = [];
				foreach (BookInline inline in paragraph.Inlines)
				{
					if (inline.Image is BookImage image)
					{
						parts.Add(new BsonDocument
						{
							{ "img", image.FileName },
							{ "w", image.Width },
							{ "h", image.Height },
							{ "alt", image.Alt is null ? BsonNull.Value : image.Alt },
						});
					}
					else
					{
						parts.Add(inline.Text ?? string.Empty);
					}
				}

				paragraphs.Add(parts);
			}

			return new BsonDocument
			{
				{ "_id", ChapterId(book.Slug, chapter.Number) },
				{ "book", book.Slug },
				{ "owner", book.Owner },
				{ "chapter", chapter.Number },
				{ "title", chapter.Title },
				{ "paragraphs", paragraphs },
			};
		}

		private static BookChapter ToChapter(BsonDocument document)
		{
			List<BookParagraph> paragraphs = [];
			if (document.TryGetValue("paragraphs", out BsonValue stored) && stored.IsBsonArray)
			{
				foreach (BsonValue value in stored.AsBsonArray)
				{
					if (value.IsString)
					{
						paragraphs.Add(BookParagraph.OfText(value.AsString));
					}
					else if (value.IsBsonArray)
					{
						List<BookInline> inlines = [];
						foreach (BsonValue part in value.AsBsonArray)
						{
							if (part.IsString)
							{
								inlines.Add(BookInline.OfText(part.AsString));
							}
							else if (part.IsBsonDocument && ToImage(part.AsBsonDocument) is BookImage image)
							{
								inlines.Add(BookInline.OfImage(image));
							}
						}

						if (inlines.Count > 0)
						{
							paragraphs.Add(new BookParagraph(inlines));
						}
					}
				}
			}

			return new BookChapter
			{
				Number = document.GetValue("chapter", 0).ToInt32(),
				Title = document.GetValue("title", BsonString.Empty).AsString,
				Paragraphs = paragraphs,
			};
		}

		private static BookImage? ToImage(BsonDocument part)
		{
			string? fileName = part.TryGetValue("img", out BsonValue name) && name.IsString ? name.AsString : null;
			if (!BookImageName.IsValid(fileName))
			{
				return null;
			}

			return new BookImage(
				fileName!,
				part.GetValue("w", 0).ToInt32(),
				part.GetValue("h", 0).ToInt32(),
				part.TryGetValue("alt", out BsonValue alt) && alt.IsString ? alt.AsString : null);
		}

		private static StoredBook ToBook(BsonDocument document) => new()
		{
			Slug = document["_id"].AsString,
			Owner = document.GetValue("owner", BsonString.Empty).AsString,
			Title = document.GetValue("title", BsonString.Empty).AsString,
			Author = document.TryGetValue("author", out BsonValue author) && author.IsString ? author.AsString : null,
			Language = document.TryGetValue("language", out BsonValue language) && language.IsString ? language.AsString : null,
			Format = document.GetValue("format", "epub").AsString,
			ChapterTitles = document.TryGetValue("chapters", out BsonValue chapters) && chapters.IsBsonArray
				? [.. chapters.AsBsonArray.Select(title => title.IsString ? title.AsString : string.Empty)]
				: [],
			UploadedAtUtc = document.TryGetValue("uploadedAt", out BsonValue uploaded) && uploaded.IsValidDateTime
				? uploaded.ToUniversalTime()
				: DateTime.UnixEpoch,
		};

		/// <summary>Idempotent; safe to call on every start.</summary>
		internal static async Task EnsureIndexesAsync(MongoClient client, CancellationToken cancellationToken = default)
		{
			IMongoDatabase database = client.GetDatabase(DatabaseName);
			IndexKeysDefinitionBuilder<BsonDocument> keys = new();

			await database.GetCollection<BsonDocument>(BooksCollectionName).Indexes.CreateOneAsync(
				new CreateIndexModel<BsonDocument>(keys.Ascending("owner"), new CreateIndexOptions { Name = "owner" }),
				cancellationToken: cancellationToken);

			// What a book's removal deletes by.
			await database.GetCollection<BsonDocument>(ChaptersCollectionName).Indexes.CreateOneAsync(
				new CreateIndexModel<BsonDocument>(keys.Ascending("book"), new CreateIndexOptions { Name = "book" }),
				cancellationToken: cancellationToken);
		}
	}
}
