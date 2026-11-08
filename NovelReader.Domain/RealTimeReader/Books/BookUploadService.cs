using NovelReader.Domain.RealTimeReader.User;

namespace NovelReader.Domain.RealTimeReader.Books
{
	public enum BookUploadFailure
	{
		None,
		/// <summary>No parser recognises the file.</summary>
		UnsupportedFormat,
		/// <summary>Recognised, but broken or not a book inside.</summary>
		Unreadable,
		/// <summary>Readable, but there was no text or picture in it to read.</summary>
		NoContent,
		TooManyBooks,
	}

	public sealed record BookUploadResult(
		BookUploadFailure Failure,
		string? Slug = null,
		string? Title = null,
		int ChapterCount = 0,
		string? Detail = null)
	{
		public bool Succeeded => Failure == BookUploadFailure.None;
	}

	/// <summary>
	/// Turns an uploaded file into a book on the reader's shelf (D50): parse it, store the
	/// pictures and the chapters, and put a bookmark at its first chapter so that it is the
	/// novel they have just started reading. The file itself is never written anywhere — it is
	/// read from memory and dropped with the request.
	/// </summary>
	public sealed class BookUploadService(
		IEnumerable<IBookParser> parsers,
		IBookStore bookStore,
		IBookImageStore imageStore,
		IReadingProgressStore readingProgressStore,
		IBookChapterClassifier? chapterClassifier = null)
	{
		/// <summary>The largest file accepted. A long illustrated light-novel volume is about 15 MB.</summary>
		public const long MaximumUploadBytes = 64L * 1024 * 1024;

		/// <summary>A shelf, not a disk to fill: each book's pictures are kept on the server.</summary>
		public const int MaximumBooksPerReader = 200;

		/// <summary>How much of the file a parser is shown to decide whether it is its format.</summary>
		private const int SniffLength = 64;

		private readonly IReadOnlyList<IBookParser> parsers = [.. parsers];

		/// <summary><paramref name="content"/> must be seekable; it is read more than once.</summary>
		public async Task<BookUploadResult> UploadAsync(
			string userName,
			string fileName,
			Stream content,
			CancellationToken cancellationToken = default)
		{
			if (await bookStore.CountForOwnerAsync(userName, cancellationToken) >= MaximumBooksPerReader)
			{
				return new BookUploadResult(BookUploadFailure.TooManyBooks);
			}

			byte[] start = new byte[SniffLength];
			content.Position = 0;
			int read = await content.ReadAtLeastAsync(start, SniffLength, throwOnEndOfStream: false, cancellationToken);
			content.Position = 0;

			IBookParser? parser = parsers.FirstOrDefault(candidate => candidate.Recognizes(fileName, start.AsSpan(0, read)));
			if (parser is null)
			{
				return new BookUploadResult(BookUploadFailure.UnsupportedFormat);
			}

			ParsedBook parsed;
			try
			{
				parsed = parser.Parse(content, cancellationToken);
			}
			catch (UnreadableBookException exception)
			{
				return new BookUploadResult(BookUploadFailure.Unreadable, Detail: exception.Message);
			}

			if (parsed.Chapters.Count == 0)
			{
				return new BookUploadResult(BookUploadFailure.NoContent);
			}

			string title = TitleOf(parsed, fileName);

			// The copyright page, the afterword and the like are kept but marked, so reading on
			// passes over them (D52). Without a classifier nothing is marked.
			BookExtrasResult extras = await BookExtras.MarkAsync(title, parsed.Chapters, chapterClassifier, cancellationToken);
			IReadOnlyList<BookChapter> chapters = extras.Chapters;

			string slug = UploadedBookSlug.New();
			StoredBook book = new()
			{
				Slug = slug,
				Owner = userName,
				Title = title,
				Author = parsed.Author,
				Language = parsed.Language,
				Format = parser.Format,
				ChapterTitles = [.. chapters.Select(chapter => chapter.Title)],
				ExtraChapters = [.. chapters.Where(chapter => chapter.IsExtra).Select(chapter => chapter.Number)],
				// Not judged — no classifier, or it did not answer for every chapter — means it is
				// judged again when the book is opened (D53).
				ExtrasJudgedAtUtc = extras.Judged ? DateTime.UtcNow : null,
				UploadedAtUtc = DateTime.UtcNow,
			};

			try
			{
				// Pictures first and the book document last: until that is written the book is not
				// on anyone's shelf, so a failure part-way leaves nothing half-there to be read.
				foreach (ParsedImage image in parsed.Images)
				{
					await imageStore.SaveAsync(slug, image.FileName, image.Bytes, cancellationToken);
				}

				await bookStore.SaveAsync(book, chapters, cancellationToken);
			}
			catch
			{
				// Not the request's token: the cleanup has to happen even when the upload was cancelled.
				await imageStore.DeleteBookAsync(slug, CancellationToken.None);
				await bookStore.DeleteAsync(userName, slug, CancellationToken.None);
				throw;
			}

			// "The novel the reader has started": a bookmark at its start, and the details the
			// library shows, so it is the novel reopened on their next visit.
			DateTime now = DateTime.UtcNow;
			await readingProgressStore.SaveAsync(userName, new ReadingProgress
			{
				NovelName = slug,
				ChapterNumber = 1,
				ParagraphNumber = 1,
				UpdatedAtUtc = now,
			}, cancellationToken);
			await readingProgressStore.SaveNovelDetailsAsync(userName, slug, new NovelSummary
			{
				Slug = slug,
				Title = book.Title,
				TotalChapters = book.ChapterTitles.Count,
			}, now, cancellationToken);

			return new BookUploadResult(BookUploadFailure.None, slug, book.Title, book.ChapterTitles.Count);
		}

		/// <summary>The book's own title, else the file's name without its extension.</summary>
		private static string TitleOf(ParsedBook parsed, string fileName)
		{
			if (!string.IsNullOrWhiteSpace(parsed.Title))
			{
				return parsed.Title.Trim();
			}

			string fromFile = Path.GetFileNameWithoutExtension(fileName).Trim();
			return fromFile.Length > 0 ? fromFile : "Untitled book";
		}
	}
}
