namespace NovelReader.Domain.RealTimeReader.Books
{
	/// <summary>
	/// A picture in a book, as a paragraph refers to it: the stored file and its size in pixels.
	/// The size travels with the text so the page can reserve the space before the picture has
	/// loaded — the reading column is measured to restore a bookmark, and a picture arriving
	/// late would move everything under it (D50).
	/// </summary>
	public sealed record BookImage(string FileName, int Width, int Height, string? Alt);

	/// <summary>One run of a paragraph: some text, or a picture. Exactly one of the two is set.</summary>
	public sealed record BookInline
	{
		public string? Text { get; private init; }
		public BookImage? Image { get; private init; }

		public static BookInline OfText(string text) => new() { Text = text };
		public static BookInline OfImage(BookImage image) => new() { Image = image };
	}

	/// <summary>
	/// A paragraph of an uploaded book. Usually one run of text; a run of text with a small
	/// picture inside it (a drawn dash, a glyph); or a picture on its own — an illustration.
	/// </summary>
	public sealed record BookParagraph(IReadOnlyList<BookInline> Inlines)
	{
		public static BookParagraph OfText(string text) => new([BookInline.OfText(text)]);

		/// <summary>A picture with nothing beside it: drawn as an illustration, across the column.</summary>
		public bool IsStandaloneImage => Inlines.Count == 1 && Inlines[0].Image is not null;

		public string PlainText => string.Concat(Inlines.Select(inline => inline.Text));
	}

	/// <summary>
	/// A chapter of an uploaded book. <see cref="Title"/> becomes paragraph 1, as a scraped
	/// chapter's first line does (D19); <see cref="Paragraphs"/> follow it from 2.
	/// </summary>
	public sealed class BookChapter
	{
		public required int Number { get; init; }
		public required string Title { get; init; }
		public required IReadOnlyList<BookParagraph> Paragraphs { get; init; }
	}

	/// <summary>A picture taken out of the uploaded file, named by its content (D50).</summary>
	public sealed class ParsedImage
	{
		/// <summary><c>{sha256}.{ext}</c> — see <see cref="BookImageName"/>.</summary>
		public required string FileName { get; init; }
		public required string MediaType { get; init; }
		public required byte[] Bytes { get; init; }
	}

	/// <summary>What a parser made of an uploaded file. Nothing of the file itself is kept.</summary>
	public sealed class ParsedBook
	{
		/// <summary>The book's own title; empty when it does not give one.</summary>
		public required string Title { get; init; }
		public string? Author { get; init; }
		public string? Language { get; init; }
		public required IReadOnlyList<BookChapter> Chapters { get; init; }
		/// <summary>Every picture the chapters refer to, once each.</summary>
		public required IReadOnlyList<ParsedImage> Images { get; init; }
	}

	/// <summary>An uploaded book as stored: whose it is, and its table of contents.</summary>
	public sealed class StoredBook
	{
		public required string Slug { get; init; }
		public required string Owner { get; init; }
		public required string Title { get; init; }
		public string? Author { get; init; }
		public string? Language { get; init; }
		/// <summary><c>epub</c>; later also <c>fb2</c>.</summary>
		public required string Format { get; init; }
		/// <summary>Chapter titles in order; chapter N is at index N - 1.</summary>
		public required IReadOnlyList<string> ChapterTitles { get; init; }
		public required DateTime UploadedAtUtc { get; init; }
	}
}
