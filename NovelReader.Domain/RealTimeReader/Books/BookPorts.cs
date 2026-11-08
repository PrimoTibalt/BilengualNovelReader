namespace NovelReader.Domain.RealTimeReader.Books
{
	/// <summary>
	/// Reads one book format into chapters and pictures. EPUB now; FB2 is meant to be a second
	/// implementation registered beside it — the upload asks each parser in turn whether it
	/// recognises the file (D50).
	/// </summary>
	public interface IBookParser
	{
		/// <summary>The format's short name, stored with the book: <c>epub</c>.</summary>
		string Format { get; }

		/// <summary>
		/// Whether this is a file of this parser's format, from its name and its first bytes
		/// (at least 64 of them when the file is that long).
		/// </summary>
		bool Recognizes(string fileName, ReadOnlySpan<byte> start);

		/// <summary>Reads the whole book. Throws <see cref="UnreadableBookException"/> when it cannot.</summary>
		ParsedBook Parse(Stream content, CancellationToken cancellationToken = default);
	}

	/// <summary>The file is of a recognised format but could not be read as a book.</summary>
	public sealed class UnreadableBookException(string message, Exception? innerException = null)
		: InvalidOperationException(message, innerException);

	/// <summary>
	/// Uploaded books: the table of contents and the chapters' text. Every read names the owner,
	/// so a book is only ever found by the reader who uploaded it.
	/// </summary>
	public interface IBookStore
	{
		/// <summary>Writes the chapters, then the book itself: a book that is listed is complete.</summary>
		Task SaveAsync(StoredBook book, IReadOnlyList<BookChapter> chapters, CancellationToken cancellationToken = default);

		Task<StoredBook?> GetAsync(string owner, string slug, CancellationToken cancellationToken = default);

		Task<BookChapter?> GetChapterAsync(string owner, string slug, int chapterNumber, CancellationToken cancellationToken = default);

		Task<long> CountForOwnerAsync(string owner, CancellationToken cancellationToken = default);

		/// <summary>The book and all its chapters. Nothing happens for a book that is not there.</summary>
		Task DeleteAsync(string owner, string slug, CancellationToken cancellationToken = default);
	}

	/// <summary>A book's pictures, by book and file name (D50).</summary>
	public interface IBookImageStore
	{
		Task SaveAsync(string bookSlug, string fileName, byte[] bytes, CancellationToken cancellationToken = default);

		/// <summary>The picture, or null when there is none by that name.</summary>
		Stream? OpenRead(string bookSlug, string fileName);

		Task DeleteBookAsync(string bookSlug, CancellationToken cancellationToken = default);
	}
}
