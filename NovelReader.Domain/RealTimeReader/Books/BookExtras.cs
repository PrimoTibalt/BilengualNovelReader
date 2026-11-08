using System.Collections.Concurrent;
using System.Text;

namespace NovelReader.Domain.RealTimeReader.Books
{
	/// <summary>
	/// What a classifier is shown of one chapter: where it sits in the book, its title, and how it
	/// begins — or, for a chapter of pictures only, how many there are (D52).
	/// </summary>
	public sealed record BookChapterSample(
		string BookTitle,
		int Number,
		int ChapterCount,
		string ChapterTitle,
		string Opening,
		int PictureCount);

	/// <summary>
	/// Tells the chapters of a book from what surrounds them: the copyright page, the contents, an
	/// afterword, the author's thanks, a newsletter, a distributor's advert (D52).
	/// </summary>
	public interface IBookChapterClassifier
	{
		/// <returns>
		/// True when the chapter is confidently not part of the book; false when it is, or might be —
		/// hiding a real chapter loses the reader a chapter, while showing a copyright page costs
		/// them a scroll. Null when there was no answer at all (the service failed or did not
		/// reply): the book is then not counted as judged, and is asked about again later (D53).
		/// </returns>
		Task<bool?> IsExtraAsync(BookChapterSample sample, CancellationToken cancellationToken = default);
	}

	/// <summary>
	/// A book's chapters with their extras marked. <see cref="Judged"/> only when every chapter got
	/// an answer: a book is stamped as judged on that alone, so an outage cannot leave one stamped
	/// with nothing marked (D53).
	/// </summary>
	public sealed record BookExtrasResult(IReadOnlyList<BookChapter> Chapters, bool Judged);

	/// <summary>
	/// Marks a book's extras (D52). Every chapter is asked about at once, a few at a time, and the
	/// whole marking has a deadline: a slow or absent classifier leaves chapters unmarked, never an
	/// upload or an opening waiting long or failed.
	/// </summary>
	public static class BookExtras
	{
		/// <summary>How long marking may take. A volume's dozen chapters take about a second.</summary>
		public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

		private const int ConcurrentQuestions = 4;

		/// <summary>The opening shown for a chapter of text: enough to say what it is, cheap to send.</summary>
		private const int OpeningParagraphs = 15;

		private const int OpeningLength = 3000;

		public static async Task<BookExtrasResult> MarkAsync(
			string bookTitle,
			IReadOnlyList<BookChapter> chapters,
			IBookChapterClassifier? classifier,
			CancellationToken cancellationToken = default)
		{
			if (classifier is null || chapters.Count == 0)
			{
				return new BookExtrasResult(chapters, Judged: false);
			}

			using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			deadline.CancelAfter(Deadline);
			using SemaphoreSlim turns = new(ConcurrentQuestions);

			bool?[] answers = await Task.WhenAll(chapters.Select(async chapter =>
			{
				// The first chapter of pictures is the cover: always the book's own, nothing to ask.
				if (chapter.Number == 1 && chapter.IsPicturesOnly)
				{
					return (bool?)false;
				}

				try
				{
					await turns.WaitAsync(deadline.Token);
					try
					{
						return await classifier.IsExtraAsync(SampleOf(bookTitle, chapters.Count, chapter), deadline.Token);
					}
					finally
					{
						turns.Release();
					}
				}
				catch (Exception) when (!cancellationToken.IsCancellationRequested)
				{
					// Past the deadline, or the classifier broke: no answer for this one.
					return null;
				}
			}));

			bool judged = answers.All(answer => answer is not null);
			bool[] extra = [.. answers.Select(answer => answer == true)];

			// A book judged to be nothing but extras has been misjudged, not emptied. It still
			// counts as judged: asking again would get the same answer.
			if (extra.All(isExtra => isExtra))
			{
				return new BookExtrasResult([.. chapters.Select(chapter => WithExtra(chapter, false))], judged);
			}

			return new BookExtrasResult([.. chapters.Select((chapter, index) => WithExtra(chapter, extra[index]))], judged);
		}

		private static BookChapter WithExtra(BookChapter chapter, bool isExtra) =>
			chapter.IsExtra == isExtra
				? chapter
				: new BookChapter { Number = chapter.Number, Title = chapter.Title, Paragraphs = chapter.Paragraphs, IsExtra = isExtra };

		internal static BookChapterSample SampleOf(string bookTitle, int chapterCount, BookChapter chapter)
		{
			StringBuilder opening = new();
			foreach (BookParagraph paragraph in chapter.Paragraphs.Where(paragraph => !paragraph.IsStandaloneImage).Take(OpeningParagraphs))
			{
				opening.AppendLine(paragraph.PlainText.Trim());
				if (opening.Length >= OpeningLength)
				{
					break;
				}
			}

			string text = opening.Length > OpeningLength ? opening.ToString(0, OpeningLength) : opening.ToString().TrimEnd();
			int pictures = chapter.Paragraphs.Sum(paragraph => paragraph.Inlines.Count(inline => inline.Image is not null));

			return new BookChapterSample(bookTitle, chapter.Number, chapterCount, chapter.Title, text, pictures);
		}
	}

	/// <summary>
	/// Judges the extras of a book that has not been through it — one uploaded before D52, or while
	/// the classifier was down — the first time it is opened (D53). Stamped only once every chapter
	/// got an answer; until then it is asked about again on the next opening.
	/// </summary>
	public sealed class BookExtrasService(IBookStore bookStore, IBookChapterClassifier? classifier = null)
	{
		/// <summary>Judgings running, by book: two openings at once share one rather than asking twice.</summary>
		private readonly ConcurrentDictionary<string, Lazy<Task<StoredBook>>> running = new(StringComparer.Ordinal);

		/// <summary>The book as it now stands: judged if it could be, as it was if it could not.</summary>
		public Task<StoredBook> EnsureJudgedAsync(StoredBook book)
		{
			if (book.ExtrasJudgedAtUtc is not null || classifier is null)
			{
				return Task.FromResult(book);
			}

			Lazy<Task<StoredBook>> judging = running.GetOrAdd(book.Slug, _ => new Lazy<Task<StoredBook>>(() => JudgeAsync(book)));
			return judging.Value;
		}

		/// <summary>
		/// Not tied to the request that started it: a second opening may be waiting on the same run,
		/// and the marking's own deadline bounds it.
		/// </summary>
		private async Task<StoredBook> JudgeAsync(StoredBook book)
		{
			try
			{
				IReadOnlyList<BookChapter> chapters = await bookStore.GetChaptersAsync(book.Owner, book.Slug);
				BookExtrasResult result = await BookExtras.MarkAsync(book.Title, chapters, classifier);
				if (!result.Judged)
				{
					return book;
				}

				IReadOnlyList<int> extras = [.. result.Chapters.Where(chapter => chapter.IsExtra).Select(chapter => chapter.Number)];
				DateTime judgedAt = DateTime.UtcNow;
				await bookStore.SaveExtrasAsync(book.Owner, book.Slug, extras, judgedAt);

				return new StoredBook
				{
					Slug = book.Slug,
					Owner = book.Owner,
					Title = book.Title,
					Author = book.Author,
					Language = book.Language,
					Format = book.Format,
					ChapterTitles = book.ChapterTitles,
					ExtraChapters = extras,
					ExtrasJudgedAtUtc = judgedAt,
					UploadedAtUtc = book.UploadedAtUtc,
				};
			}
			finally
			{
				running.TryRemove(book.Slug, out _);
			}
		}
	}
}
