using System.IO.Compression;
using System.Text;
using NovelReader.Books;
using NovelReader.Domain.RealTimeReader.Books;
using NovelReader.Domain.RealTimeReader.Parsing;
using NovelReader.Domain.RealTimeReader.Reading;
using NovelReader.Domain.RealTimeReader.User;

namespace NovelReader.Tests
{
	/// <summary>Uploaded EPUBs: parsed into chapters by their table of contents, stored, and read back (D50).</summary>
	public class BookTests
	{
		// ---- Building EPUBs to read ----

		/// <summary>A PNG header: enough for the sniffer, which reads only the size.</summary>
		private static byte[] Png(int width, int height, byte salt = 0)
		{
			byte[] bytes = new byte[33];
			new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
			System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
			System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
			bytes[32] = salt;
			return bytes;
		}

		private static string Page(string body) =>
			$$"""<?xml version="1.0" encoding="utf-8"?><!DOCTYPE html><html xmlns="http://www.w3.org/1999/xhtml"><head><title>t</title><style>p{color:red}</style></head><body>{{body}}</body></html>""";

		/// <summary>
		/// An EPUB 3 laid out the way the Re:Zero volumes are: a cover, a contents page, and a
		/// chapter whose text is split over two files with an illustration's page between them.
		/// </summary>
		private static MemoryStream SampleEpub(bool withNav = true, bool withNcx = false)
		{
			Dictionary<string, object> files = new()
			{
				["mimetype"] = "application/epub+zip",
				["META-INF/container.xml"] = """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/package.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""",
				["OEBPS/package.opf"] = $"""
					<?xml version="1.0" encoding="utf-8"?>
					<package version="3.0" xmlns="http://www.idpf.org/2007/opf">
					  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
					    <dc:title>A Sample   Book</dc:title><dc:creator>Some Author</dc:creator><dc:language>en</dc:language>
					  </metadata>
					  <manifest>
					    {(withNav ? """<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>""" : "")}
					    {(withNcx ? """<item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>""" : "")}
					    <item id="cover" href="cover.xhtml" media-type="application/xhtml+xml"/>
					    <item id="contents" href="contents.xhtml" media-type="application/xhtml+xml"/>
					    <item id="c1a" href="text/c1a.xhtml" media-type="application/xhtml+xml"/>
					    <item id="art" href="text/art.xhtml" media-type="application/xhtml+xml"/>
					    <item id="c1b" href="text/c1b.xhtml" media-type="application/xhtml+xml"/>
					    <item id="note" href="note.xhtml" media-type="application/xhtml+xml"/>
					    <item id="c2" href="text/c%202.xhtml" media-type="application/xhtml+xml"/>
					  </manifest>
					  <spine{(withNcx ? " toc=\"ncx\"" : "")}>
					    <itemref idref="cover"/><itemref idref="contents"/><itemref idref="c1a"/><itemref idref="art"/>
					    <itemref idref="c1b"/><itemref idref="note" linear="no"/><itemref idref="c2"/>
					  </spine>
					  <guide><reference type="toc" href="contents.xhtml"/></guide>
					</package>
					""",
				["OEBPS/nav.xhtml"] = """
					<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><body>
					<nav epub:type="toc"><ol>
					  <li><a href="text/c1a.xhtml">Chapter 1: The Start</a></li>
					  <li><a href="text/c1a.xhtml#section">A section inside chapter 1</a></li>
					  <li><a href="text/c%202.xhtml">Chapter 2: Tom &amp; Jerry</a></li>
					</ol></nav></body></html>
					""",
				["OEBPS/toc.ncx"] = """
					<?xml version="1.0"?><!DOCTYPE ncx PUBLIC "-//NISO//DTD ncx 2005-1//EN" "http://www.daisy.org/z3986/2005/ncx-2005-1.dtd">
					<ncx xmlns="http://www.daisy.org/z3986/2005/ncx/"><navMap>
					  <navPoint id="a"><navLabel><text>Chapter 1: The Start</text></navLabel><content src="text/c1a.xhtml"/></navPoint>
					  <navPoint id="b"><navLabel><text>Chapter 2: Tom &amp; Jerry</text></navLabel><content src="text/c%202.xhtml"/></navPoint>
					</navMap></ncx>
					""",
				["OEBPS/cover.xhtml"] = Page("""<svg xmlns:xlink="http://www.w3.org/1999/xlink"><image xlink:href="images/cover.png"/></svg>"""),
				["OEBPS/contents.xhtml"] = Page("""<p><a href="text/c1a.xhtml">Chapter 1</a></p>"""),
				["OEBPS/text/c1a.xhtml"] = Page("""
					<h1 class="chapter-number"><a id="page-1"/>CHAPTER 1</h1>
					<h1 class="chapter-title">THE START</h1>
					<h2>1</h2>
					<p><span>It was a </span><em>dark</em><span> night.</span></p>
					<p>“<img class="inline3" src="../images/dash.png" alt="image"/>”</p>
					<p>Line one<br/>Line two</p>
					<hr/>
					<p>   </p>
					<p>Missing <img src="../images/nowhere.png"/>picture.</p>
					"""),
				["OEBPS/text/art.xhtml"] = Page("""<div class="image_full"><img src="../images/art.png" alt="A &quot;fine&quot; picture"/></div>"""),
				["OEBPS/text/c1b.xhtml"] = Page("""<p id="later">The night went on.</p><p><img src="../images/art.png"/><img src="../images/cover.png"/></p>"""),
				["OEBPS/note.xhtml"] = Page("<p>A footnote, not in the reading order.</p>"),
				["OEBPS/text/c 2.xhtml"] = Page("<h1>Chapter 2</h1><h1>Tom &amp; Jerry</h1><p>Chapter two&#8217;s text.</p><script>alert(1)</script>"),
				["OEBPS/images/cover.png"] = Png(1200, 1800),
				["OEBPS/images/art.png"] = Png(1800, 1350, salt: 1),
				["OEBPS/images/dash.png"] = Png(111, 2, salt: 2),
			};

			MemoryStream stream = new();
			using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
			{
				foreach ((string name, object content) in files)
				{
					// Stored, as EPUB requires of `mimetype`, so it can be recognised from the first bytes.
					CompressionLevel level = name == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
					using Stream entry = zip.CreateEntry(name, level).Open();
					byte[] bytes = content as byte[] ?? Encoding.UTF8.GetBytes(((string)content).Trim());
					entry.Write(bytes);
				}
			}

			stream.Position = 0;
			return stream;
		}

		private static string Describe(BookParagraph paragraph) =>
			string.Concat(paragraph.Inlines.Select(inline => inline.Image is { } image ? $"[{image.Width}x{image.Height}]" : inline.Text));

		// ---- Parsing ----

		[Fact]
		public void Chapters_follow_the_table_of_contents_across_files()
		{
			ParsedBook book = new EpubParser().Parse(SampleEpub());

			Assert.Equal("A Sample Book", book.Title);
			Assert.Equal("Some Author", book.Author);
			Assert.Equal("en", book.Language);

			// The cover, before the first entry, is a chapter named after the book; the contents
			// page is gone; the second entry into chapter 1's second file does not start a chapter.
			Assert.Equal(["A Sample Book", "Chapter 1: The Start", "Chapter 2: Tom & Jerry"], book.Chapters.Select(chapter => chapter.Title));
			Assert.Equal([1, 2, 3], book.Chapters.Select(chapter => chapter.Number));
			Assert.Equal(["[1200x1800]"], book.Chapters[0].Paragraphs.Select(Describe));
		}

		[Fact]
		public void A_chapter_keeps_its_text_and_pictures_in_order()
		{
			ParsedBook book = new EpubParser().Parse(SampleEpub());

			Assert.Equal(
				[
					// `CHAPTER 1` and `THE START` repeat the title and go; the section number stays.
					"1",
					"It was a dark night.",
					"“[111x2]”",
					"Line one",
					"Line two",
					XhtmlParagraphs.SceneBreak,
					// A picture that is not in the archive leaves its text.
					"Missing picture.",
					"[1800x1350]",
					"The night went on.",
					// Two pictures side by side are two illustrations.
					"[1800x1350]",
					"[1200x1800]",
				],
				book.Chapters[1].Paragraphs.Select(Describe));

			Assert.Equal("A \"fine\" picture", book.Chapters[1].Paragraphs[7].Inlines[0].Image!.Alt);
			Assert.Equal(["Chapter two’s text."], book.Chapters[2].Paragraphs.Select(Describe));
		}

		[Fact]
		public void Each_picture_is_kept_once_named_by_its_content()
		{
			ParsedBook book = new EpubParser().Parse(SampleEpub());

			Assert.Equal(3, book.Images.Count);
			Assert.All(book.Images, image => Assert.True(BookImageName.IsValid(image.FileName)));
			Assert.All(book.Images, image => Assert.Equal("image/png", image.MediaType));
			Assert.Equal(BookImageName.For(Png(1200, 1800), "png"), book.Chapters[0].Paragraphs[0].Inlines[0].Image!.FileName);
		}

		[Fact]
		public void An_epub2_table_of_contents_is_read_from_the_ncx()
		{
			ParsedBook book = new EpubParser().Parse(SampleEpub(withNav: false, withNcx: true));

			Assert.Equal(["A Sample Book", "Chapter 1: The Start", "Chapter 2: Tom & Jerry"], book.Chapters.Select(chapter => chapter.Title));
		}

		[Fact]
		public void Without_a_table_of_contents_every_file_is_a_chapter_named_by_its_heading()
		{
			ParsedBook book = new EpubParser().Parse(SampleEpub(withNav: false));

			// cover, chapter 1 (two files and the picture between), chapter 2; the contents page is still left out.
			Assert.Equal(["Part 1", "CHAPTER 1", "Part 3", "Part 4", "Chapter 2"], book.Chapters.Select(chapter => chapter.Title));
		}

		[Fact]
		public void Recognizes_an_epub_by_its_signature_or_its_name()
		{
			EpubParser parser = new();
			byte[] start = new byte[64];
			SampleEpub().ReadExactly(start);

			Assert.True(parser.Recognizes("whatever.bin", start));
			Assert.True(parser.Recognizes("book.EPUB", "PK\u0003\u0004"u8));
			Assert.False(parser.Recognizes("book.epub", "%PDF-1.7"u8));
			Assert.False(parser.Recognizes("archive.zip", "PK\u0003\u0004"u8));
		}

		[Fact]
		public void A_file_that_is_not_an_epub_inside_is_unreadable()
		{
			Assert.Throws<UnreadableBookException>(() => new EpubParser().Parse(new MemoryStream("not a zip at all"u8.ToArray())));

			MemoryStream emptyZip = new();
			using (new ZipArchive(emptyZip, ZipArchiveMode.Create, leaveOpen: true)) { }
			emptyZip.Position = 0;
			Assert.Throws<UnreadableBookException>(() => new EpubParser().Parse(emptyZip));
		}

		[Theory]
		[InlineData("", "OEBPS/a.xhtml", "OEBPS/a.xhtml")]
		[InlineData("OEBPS/text", "../images/x%20y.jpg#frag", "OEBPS/images/x y.jpg")]
		[InlineData("OEBPS", "../../../etc/passwd", "etc/passwd")]
		[InlineData("OEBPS", "https://example.com/a.jpg", null)]
		[InlineData("OEBPS", "data:image/png;base64,AAAA", null)]
		[InlineData("OEBPS", "#only-a-fragment", null)]
		public void Hrefs_resolve_inside_the_archive(string directory, string href, string? expected)
		{
			Assert.Equal(expected, EpubReader.Resolve(directory, href));
		}

		[Fact]
		public void The_sniffer_reads_sizes_and_refuses_what_it_does_not_serve()
		{
			Assert.Equal(new SniffedImage("png", 640, 480), ImageSniffer.Sniff(Png(640, 480)));
			Assert.Equal(new SniffedImage("gif", 3, 2), ImageSniffer.Sniff("GIF89a\u0003\u0000\u0002\u0000"u8));

			byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x07, 0x08, 0x04, 0xB0, 0x03, 0, 0, 0];
			Assert.Equal(new SniffedImage("jpg", 1200, 1800), ImageSniffer.Sniff(jpeg));

			Assert.Null(ImageSniffer.Sniff("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8));
			Assert.Null(ImageSniffer.Sniff(Png(0, 10)));
		}

		// ---- Markup ----

		[Fact]
		public void Pictures_are_marked_up_lazily_with_their_size_and_escaped()
		{
			string slug = "book--0123456789abcdef";
			string file = new string('a', 64) + ".png";
			Dictionary<string, string> known = new() { ["night"] = "night" };

			string figure = ParagraphMarkupBuilder.BuildMarkup(
				new BookParagraph([BookInline.OfImage(new BookImage(file, 1800, 1350, "<b>\"x\"</b>"))]), known, slug);
			Assert.Equal(
				$"<img class=\"book-image book-image--figure\" src=\"books/{slug}/images/{file}\" width=\"1800\" height=\"1350\" alt=\"&lt;b&gt;&quot;x&quot;&lt;/b&gt;\" loading=\"lazy\" decoding=\"async\">",
				figure);

			string inline = ParagraphMarkupBuilder.BuildMarkup(
				new BookParagraph([BookInline.OfText("A <night> “"), BookInline.OfImage(new BookImage(file, 111, 2, null)), BookInline.OfText("”")]), known, slug);
			Assert.StartsWith("A &lt;<span class=\"known-word\" data-term=\"night\">night</span>&gt; “<img class=\"book-image book-image--inline\"", inline);
			Assert.EndsWith("decoding=\"async\">”", inline);

			string ornament = ParagraphMarkupBuilder.BuildMarkup(
				new BookParagraph([BookInline.OfImage(new BookImage(file, 192, 192, null))]), known, slug);
			Assert.Contains("book-image--ornament", ornament);
		}

		// ---- Uploading and reading ----

		private const string Reader = "anton";

		private sealed class Shelf : IBookStore, IBookImageStore
		{
			public Dictionary<string, (StoredBook Book, IReadOnlyList<BookChapter> Chapters)> Books { get; } = [];
			public Dictionary<string, byte[]> Images { get; } = [];
			public bool FailChapterWrites { get; init; }
			public int ExtraBooks { get; init; }

			public Task SaveAsync(StoredBook book, IReadOnlyList<BookChapter> chapters, CancellationToken cancellationToken = default)
			{
				if (FailChapterWrites) throw new IOException("simulated database outage");
				Books[book.Slug] = (book, chapters);
				return Task.CompletedTask;
			}

			public Task<StoredBook?> GetAsync(string owner, string slug, CancellationToken cancellationToken = default) =>
				Task.FromResult(Books.TryGetValue(slug, out var found) && found.Book.Owner == owner ? found.Book : null);

			public Task<BookChapter?> GetChapterAsync(string owner, string slug, int chapterNumber, CancellationToken cancellationToken = default) =>
				Task.FromResult(Books.TryGetValue(slug, out var found) && found.Book.Owner == owner
					? found.Chapters.FirstOrDefault(chapter => chapter.Number == chapterNumber)
					: null);

			public Task<IReadOnlyList<BookChapter>> GetChaptersAsync(string owner, string slug, CancellationToken cancellationToken = default) =>
				Task.FromResult(Books.TryGetValue(slug, out var found) && found.Book.Owner == owner ? found.Chapters : (IReadOnlyList<BookChapter>)[]);

			public Task SaveExtrasAsync(string owner, string slug, IReadOnlyList<int> extraChapters, DateTime judgedAtUtc, CancellationToken cancellationToken = default)
			{
				(StoredBook book, IReadOnlyList<BookChapter> chapters) = Books[slug];
				Books[slug] = (
					new StoredBook
					{
						Slug = book.Slug, Owner = book.Owner, Title = book.Title, Format = book.Format, ChapterTitles = book.ChapterTitles,
						UploadedAtUtc = book.UploadedAtUtc, ExtraChapters = extraChapters, ExtrasJudgedAtUtc = judgedAtUtc,
					},
					[.. chapters.Select(chapter => new BookChapter
					{
						Number = chapter.Number, Title = chapter.Title, Paragraphs = chapter.Paragraphs, IsExtra = extraChapters.Contains(chapter.Number),
					})]);
				return Task.CompletedTask;
			}

			public Task<long> CountForOwnerAsync(string owner, CancellationToken cancellationToken = default) =>
				Task.FromResult((long)(ExtraBooks + Books.Values.Count(entry => entry.Book.Owner == owner)));

			public Task DeleteAsync(string owner, string slug, CancellationToken cancellationToken = default)
			{
				Books.Remove(slug);
				return Task.CompletedTask;
			}

			public Task SaveAsync(string bookSlug, string fileName, byte[] bytes, CancellationToken cancellationToken = default)
			{
				Images[$"{bookSlug}/{fileName}"] = bytes;
				return Task.CompletedTask;
			}

			public Stream? OpenRead(string bookSlug, string fileName) =>
				Images.TryGetValue($"{bookSlug}/{fileName}", out byte[]? bytes) ? new MemoryStream(bytes) : null;

			public Task DeleteBookAsync(string bookSlug, CancellationToken cancellationToken = default)
			{
				foreach (string key in Images.Keys.Where(key => key.StartsWith(bookSlug + "/", StringComparison.Ordinal)).ToList())
				{
					Images.Remove(key);
				}

				return Task.CompletedTask;
			}
		}

		private sealed class RecordingProgressStore : IReadingProgressStore
		{
			public List<ReadingProgress> Saved { get; } = [];
			public List<NovelSummary?> Details { get; } = [];

			public Task SaveAsync(string userName, ReadingProgress progress, CancellationToken cancellationToken = default)
			{
				Saved.Add(progress);
				return Task.CompletedTask;
			}

			public Task SaveNovelDetailsAsync(string userName, string novelName, NovelSummary? summary, DateTime checkedAtUtc, CancellationToken cancellationToken = default)
			{
				Details.Add(summary);
				return Task.CompletedTask;
			}

			public Task<ReadingProgress?> GetAsync(string userName, string novelName, CancellationToken cancellationToken = default) => Task.FromResult<ReadingProgress?>(null);
			public Task<IReadOnlyList<NovelSummary>> GetNovelsReadAsync(string userName, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<NovelSummary>>([]);
			public Task<ReadingProgress?> GetMostRecentAsync(string userName, CancellationToken cancellationToken = default) => Task.FromResult<ReadingProgress?>(null);
		}

		private static BookUploadService Uploads(Shelf shelf, RecordingProgressStore? progress = null) =>
			new([new EpubParser()], shelf, shelf, progress ?? new RecordingProgressStore());

		[Fact]
		public async Task An_upload_is_stored_and_becomes_the_novel_being_read()
		{
			Shelf shelf = new();
			RecordingProgressStore progress = new();

			BookUploadResult result = await Uploads(shelf, progress).UploadAsync(Reader, "sample.epub", SampleEpub());

			Assert.True(result.Succeeded);
			Assert.True(UploadedBookSlug.IsUploaded(result.Slug));
			Assert.True(NovelSlug.IsValid(result.Slug));
			Assert.Equal("A Sample Book", result.Title);
			Assert.Equal(3, result.ChapterCount);

			(StoredBook book, IReadOnlyList<BookChapter> chapters) = shelf.Books[result.Slug!];
			Assert.Equal(Reader, book.Owner);
			Assert.Equal("epub", book.Format);
			Assert.Equal(3, chapters.Count);
			Assert.Equal(3, shelf.Images.Count);

			ReadingProgress bookmark = Assert.Single(progress.Saved);
			Assert.Equal((result.Slug, 1, 1), (bookmark.NovelName, bookmark.ChapterNumber, bookmark.ParagraphNumber));
			NovelSummary details = Assert.Single(progress.Details)!;
			Assert.Equal(("A Sample Book", 3), (details.Title, details.TotalChapters));
		}

		[Fact]
		public async Task A_failed_store_leaves_no_pictures_behind()
		{
			Shelf shelf = new() { FailChapterWrites = true };

			await Assert.ThrowsAsync<IOException>(() => Uploads(shelf).UploadAsync(Reader, "sample.epub", SampleEpub()));

			Assert.Empty(shelf.Images);
			Assert.Empty(shelf.Books);
		}

		[Fact]
		public async Task Uploads_that_are_not_books_are_refused_with_a_reason()
		{
			Shelf shelf = new();

			BookUploadResult pdf = await Uploads(shelf).UploadAsync(Reader, "paper.pdf", new MemoryStream("%PDF-1.7 ..."u8.ToArray()));
			Assert.Equal(BookUploadFailure.UnsupportedFormat, pdf.Failure);

			BookUploadResult broken = await Uploads(shelf).UploadAsync(Reader, "broken.epub", new MemoryStream("PK\u0003\u0004 and then nothing"u8.ToArray()));
			Assert.Equal(BookUploadFailure.Unreadable, broken.Failure);

			BookUploadResult full = await Uploads(new Shelf { ExtraBooks = BookUploadService.MaximumBooksPerReader })
				.UploadAsync(Reader, "sample.epub", SampleEpub());
			Assert.Equal(BookUploadFailure.TooManyBooks, full.Failure);

			Assert.Empty(shelf.Images);
		}

		[Fact]
		public async Task A_book_chapter_is_read_from_the_shelf_and_never_scraped()
		{
			Shelf shelf = new();
			BookUploadResult uploaded = await Uploads(shelf).UploadAsync(Reader, "sample.epub", SampleEpub());
			CountingParagraphsRetriever retriever = new();
			ChapterPreparationService preparation = new(
				new FakeNovelRepository(new FakeCollectionOfChapters()), retriever, new EmptyVocabularyRepository(), bookStore: shelf);

			PreparedChapter chapter = await preparation.PrepareAsync(Reader, uploaded.Slug!, 2);

			// The title is paragraph 1, as a scraped chapter's first line is (D19).
			Assert.Equal("Chapter 1: The Start", chapter.Paragraphs[1]);
			Assert.Equal("It was a dark night.", chapter.Paragraphs[3]);
			Assert.Contains("book-image--figure", chapter.Paragraphs[9]);
			Assert.Equal(12, chapter.Paragraphs.Count);
			Assert.Equal(0, retriever.TotalCalls);

			// Past the end, and someone else's book, are both just not there.
			await Assert.ThrowsAsync<ChapterNotFoundException>(() => preparation.PrepareAsync(Reader, uploaded.Slug!, 4));
			await Assert.ThrowsAsync<ChapterNotFoundException>(() => preparation.PrepareAsync("someone-else", uploaded.Slug!, 1));
			Assert.Equal(0, retriever.TotalCalls);
		}

		[Fact]
		public async Task A_book_on_the_shelf_is_never_looked_up_in_the_catalogue()
		{
			InMemoryReadingProgressStore progress = new();
			progress.Library.Add(new NovelSummary { Slug = UploadedBookSlug.New(), Title = "A Sample Book" });
			progress.Library.Add(new NovelSummary { Slug = "reverend-insanity" });
			DeferredBackgroundWorkScheduler scheduler = new();

			await new NovelLibraryService(progress, new StubSearchNovelsRetriever(), scheduler).GetLibraryAsync(Reader);

			// Only the site novel, which has never been looked up, is refreshed.
			Assert.Equal(1, scheduler.ScheduledCount);
			Assert.Contains("reverend-insanity", Assert.Single(scheduler.Descriptions));
		}

		// ---- Extras (D52) ----

		/// <summary>Says "extra" for the titles it is given, and records what it was asked.</summary>
		private sealed class TitleClassifier(params string[] extras) : IBookChapterClassifier
		{
			public List<BookChapterSample> Asked { get; } = [];
			public TimeSpan Delay { get; init; }
			public bool Throws { get; init; }
			/// <summary>Answers nothing, as Jev does when it cannot be reached (D53).</summary>
			public bool Silent { get; init; }

			public async Task<bool?> IsExtraAsync(BookChapterSample sample, CancellationToken cancellationToken = default)
			{
				lock (Asked) Asked.Add(sample);
				if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
				if (Throws) throw new HttpRequestException("simulated outage");
				return Silent ? null : extras.Contains(sample.ChapterTitle);
			}
		}

		private static BookChapter Chapter(int number, string title, params BookParagraph[] paragraphs) =>
			new() { Number = number, Title = title, Paragraphs = paragraphs };

		private static BookParagraph Picture() =>
			new([BookInline.OfImage(new BookImage(new string('b', 64) + ".jpg", 1200, 1800, null))]);

		[Fact]
		public async Task Chapters_judged_extras_are_marked_and_the_cover_is_never_asked_about()
		{
			BookChapter[] chapters =
			[
				Chapter(1, "Cover", Picture()),
				Chapter(2, "Copyright", BookParagraph.OfText("Copyright © 2021")),
				Chapter(3, "Chapter 1", BookParagraph.OfText("It began.")),
				Chapter(4, "Afterword", BookParagraph.OfText("Thank you for reading!")),
			];
			TitleClassifier classifier = new("Cover", "Copyright", "Afterword");

			BookExtrasResult marked = await BookExtras.MarkAsync("A Book", chapters, classifier);

			Assert.True(marked.Judged);
			Assert.Equal([false, true, false, true], marked.Chapters.Select(chapter => chapter.IsExtra));
			Assert.DoesNotContain(classifier.Asked, sample => sample.ChapterTitle == "Cover");
			BookChapterSample copyright = Assert.Single(classifier.Asked, sample => sample.ChapterTitle == "Copyright");
			Assert.Equal(("A Book", 2, 4, "Copyright © 2021", 0), (copyright.BookTitle, copyright.Number, copyright.ChapterCount, copyright.Opening, copyright.PictureCount));
		}

		[Fact]
		public async Task A_failing_or_slow_classifier_hides_nothing_and_never_holds_the_upload()
		{
			BookChapter[] chapters = [Chapter(1, "Chapter 1", BookParagraph.OfText("a")), Chapter(2, "Afterword", BookParagraph.OfText("b"))];

			// Neither a broken, a silent nor a slow classifier hides anything, and none counts as judged.
			foreach (TitleClassifier classifier in new[] { new TitleClassifier("Afterword") { Throws = true }, new TitleClassifier("Afterword") { Silent = true } })
			{
				BookExtrasResult result = await BookExtras.MarkAsync("A Book", chapters, classifier);
				Assert.False(result.Judged);
				Assert.All(result.Chapters, chapter => Assert.False(chapter.IsExtra));
			}

			System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
			BookExtrasResult slow = await BookExtras.MarkAsync(
				"A Book", chapters, new TitleClassifier("Afterword") { Delay = TimeSpan.FromMinutes(5) });
			Assert.False(slow.Judged);
			Assert.All(slow.Chapters, chapter => Assert.False(chapter.IsExtra));
			Assert.True(clock.Elapsed < BookExtras.Deadline + TimeSpan.FromSeconds(5));
		}

		[Fact]
		public async Task A_book_judged_all_extras_keeps_every_chapter()
		{
			BookChapter[] chapters = [Chapter(1, "One", BookParagraph.OfText("a")), Chapter(2, "Two", BookParagraph.OfText("b"))];

			BookExtrasResult marked = await BookExtras.MarkAsync("A Book", chapters, new TitleClassifier("One", "Two"));

			Assert.True(marked.Judged);
			Assert.All(marked.Chapters, chapter => Assert.False(chapter.IsExtra));
		}

		[Fact]
		public async Task An_upload_records_its_extras_and_still_starts_at_chapter_one()
		{
			Shelf shelf = new();
			RecordingProgressStore progress = new();
			BookUploadService uploads = new([new EpubParser()], shelf, shelf, progress, new TitleClassifier("Chapter 2: Tom & Jerry"));

			BookUploadResult result = await uploads.UploadAsync(Reader, "sample.epub", SampleEpub());

			(StoredBook book, IReadOnlyList<BookChapter> chapters) = shelf.Books[result.Slug!];
			Assert.Equal([3], book.ExtraChapters);
			Assert.NotNull(book.ExtrasJudgedAtUtc);
			Assert.True(chapters[2].IsExtra);
			Assert.Equal(3, result.ChapterCount);
			Assert.Equal(1, Assert.Single(progress.Saved).ChapterNumber);
		}

		// ---- Books not yet judged (D53) ----

		private static async Task<(Shelf Shelf, StoredBook Book)> UploadedWithout(IBookChapterClassifier? classifier)
		{
			Shelf shelf = new();
			BookUploadResult result = await new BookUploadService([new EpubParser()], shelf, shelf, new RecordingProgressStore(), classifier)
				.UploadAsync(Reader, "sample.epub", SampleEpub());
			return (shelf, shelf.Books[result.Slug!].Book);
		}

		[Fact]
		public async Task A_book_uploaded_without_answers_is_not_stamped()
		{
			(_, StoredBook withoutJev) = await UploadedWithout(null);
			(_, StoredBook duringOutage) = await UploadedWithout(new TitleClassifier("Chapter 2: Tom & Jerry") { Silent = true });

			Assert.Null(withoutJev.ExtrasJudgedAtUtc);
			Assert.Null(duringOutage.ExtrasJudgedAtUtc);
			Assert.Empty(duringOutage.ExtraChapters);
		}

		[Fact]
		public async Task An_unjudged_book_is_judged_and_stamped_when_opened_and_never_asked_about_again()
		{
			(Shelf shelf, StoredBook book) = await UploadedWithout(null);
			TitleClassifier classifier = new("Chapter 2: Tom & Jerry");
			BookExtrasService extras = new(shelf, classifier);

			StoredBook judged = await extras.EnsureJudgedAsync(book);

			Assert.Equal([3], judged.ExtraChapters);
			Assert.NotNull(judged.ExtrasJudgedAtUtc);
			Assert.Equal([3], shelf.Books[book.Slug].Book.ExtraChapters);
			Assert.True(shelf.Books[book.Slug].Chapters[2].IsExtra);
			// The cover is never asked about; the other two are, once.
			Assert.Equal(2, classifier.Asked.Count);

			StoredBook again = await extras.EnsureJudgedAsync(shelf.Books[book.Slug].Book);
			Assert.Same(shelf.Books[book.Slug].Book, again);
			Assert.Equal(2, classifier.Asked.Count);
		}

		[Fact]
		public async Task Two_openings_at_once_share_one_judging()
		{
			(Shelf shelf, StoredBook book) = await UploadedWithout(null);
			TitleClassifier classifier = new("Chapter 2: Tom & Jerry") { Delay = TimeSpan.FromMilliseconds(200) };
			BookExtrasService extras = new(shelf, classifier);

			StoredBook[] both = await Task.WhenAll(extras.EnsureJudgedAsync(book), extras.EnsureJudgedAsync(book));

			Assert.All(both, judged => Assert.Equal([3], judged.ExtraChapters));
			Assert.Equal(2, classifier.Asked.Count);
		}

		[Fact]
		public async Task A_judging_without_answers_leaves_the_book_to_be_tried_again()
		{
			(Shelf shelf, StoredBook book) = await UploadedWithout(null);

			StoredBook untouched = await new BookExtrasService(shelf, new TitleClassifier("Chapter 2: Tom & Jerry") { Silent = true }).EnsureJudgedAsync(book);
			Assert.Null(untouched.ExtrasJudgedAtUtc);
			Assert.Null(shelf.Books[book.Slug].Book.ExtrasJudgedAtUtc);

			// Next opening, with the classifier back.
			StoredBook judged = await new BookExtrasService(shelf, new TitleClassifier("Chapter 2: Tom & Jerry")).EnsureJudgedAsync(book);
			Assert.Equal([3], judged.ExtraChapters);
		}

		[Fact]
		public async Task Without_a_classifier_an_unjudged_book_is_left_as_it_is()
		{
			(Shelf shelf, StoredBook book) = await UploadedWithout(null);

			Assert.Same(book, await new BookExtrasService(shelf).EnsureJudgedAsync(book));
		}

		[Fact]
		public void Book_slugs_cannot_be_site_slugs_and_are_random()
		{
			string slug = UploadedBookSlug.New();

			Assert.Matches("^book--[0-9a-f]{16}$", slug);
			Assert.NotEqual(slug, UploadedBookSlug.New());
			Assert.False(UploadedBookSlug.IsUploaded("book-of-the-dead"));
			Assert.False(UploadedBookSlug.IsUploaded("book--../../etc"));
		}

		[Fact]
		public async Task The_image_store_keeps_pictures_per_book_and_refuses_other_paths()
		{
			string root = Path.Combine(Path.GetTempPath(), "novelreader-tests-" + Guid.NewGuid().ToString("N"));
			try
			{
				FileSystemBookImageStore store = new(root);
				string slug = UploadedBookSlug.New();
				byte[] picture = Png(10, 10);
				string name = BookImageName.For(picture, "png");

				await store.SaveAsync(slug, name, picture);
				using (Stream? read = store.OpenRead(slug, name))
				{
					Assert.NotNull(read);
					Assert.Equal(picture.Length, read.Length);
				}

				Assert.Null(store.OpenRead(slug, "../../secret.png"));
				Assert.Null(store.OpenRead("../" + slug, name));
				await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(slug, "../x.png", picture));

				await store.DeleteBookAsync(slug);
				Assert.Null(store.OpenRead(slug, name));
			}
			finally
			{
				if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
			}
		}
	}
}
