using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;
using NovelReader.Domain.RealTimeReader.Books;

namespace NovelReader.Books
{
	/// <summary>
	/// Reads an EPUB (2 or 3) into chapters (D50).
	///
	/// <list type="bullet">
	/// <item>The <b>spine</b> gives the reading order. Non-linear items (pop-up notes) and the
	/// book's own table-of-contents page — whose links mean nothing here — are left out.</item>
	/// <item>The <b>table of contents</b> (the EPUB 3 nav document, else the EPUB 2 NCX) decides
	/// where chapters begin and what they are called. A chapter runs from the spine item its
	/// entry points at up to the next entry's, so a chapter split over seven files with
	/// illustrations between them is one chapter. Whatever comes before the first entry — a cover,
	/// usually — is a chapter titled with the book's title. Without a usable table of contents,
	/// every spine item is a chapter of its own.</item>
	/// <item>The title the entry gives becomes paragraph 1, so the headings at the top of the
	/// chapter that only repeat it are dropped (<see cref="WithoutTitleEcho"/>).</item>
	/// <item>Pictures are kept when they are JPEG, PNG, GIF or WebP, named by their content.</item>
	/// </list>
	///
	/// The archive is uploaded by a reader, so it is read with limits: on the number of entries, on
	/// each entry's unpacked size and on the total unpacked, so a small file cannot unpack into an
	/// exhausted server. Entry names never become file paths.
	/// </summary>
	public sealed class EpubParser : IBookParser
	{
		public string Format => "epub";

		public bool Recognizes(string fileName, ReadOnlySpan<byte> start)
		{
			bool isZip = start.Length >= 4 && start[0] == (byte)'P' && start[1] == (byte)'K' && start[2] == 3 && start[3] == 4;
			if (!isZip)
			{
				return false;
			}

			// An EPUB's first entry is `mimetype`, stored uncompressed, so this is in the first bytes.
			return start.IndexOf("application/epub+zip"u8) >= 0
				|| fileName.EndsWith(".epub", StringComparison.OrdinalIgnoreCase);
		}

		public ParsedBook Parse(Stream content, CancellationToken cancellationToken = default)
		{
			ZipArchive archive;
			try
			{
				archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
			}
			catch (InvalidDataException exception)
			{
				throw new UnreadableBookException("The file is not a zip archive, as an EPUB is.", exception);
			}

			using (archive)
			{
				try
				{
					return new EpubReader(archive, cancellationToken).Read();
				}
				catch (InvalidDataException exception)
				{
					throw new UnreadableBookException("A part of the file is damaged.", exception);
				}
				catch (XmlException exception)
				{
					throw new UnreadableBookException($"The book's description is not valid XML: {exception.Message}", exception);
				}
			}
		}
	}

	internal sealed partial class EpubReader(ZipArchive archive, CancellationToken cancellationToken)
	{
		private const int MaximumEntries = 20_000;
		private const long MaximumEntryBytes = 64L * 1024 * 1024;
		private const long MaximumTotalBytes = 512L * 1024 * 1024;

		private long totalRead;
		private Dictionary<string, ZipArchiveEntry>? entries;
		private Dictionary<string, ZipArchiveEntry>? entriesIgnoringCase;

		private sealed record ManifestItem(string Id, string Path, string MediaType, string Properties);

		private sealed record TocEntry(string Title, string Path);

		private sealed record SpineDocument(string Path, List<RawParagraph> Paragraphs);

		[GeneratedRegex(@"\s+")]
		private static partial Regex Whitespace { get; }

		[GeneratedRegex(@"[\p{L}\p{N}]+")]
		private static partial Regex Word { get; }

		public ParsedBook Read()
		{
			if (archive.Entries.Count > MaximumEntries)
			{
				throw new UnreadableBookException("The file holds too many parts to be a book.");
			}

			string packagePath = FindPackagePath();
			XDocument package = LoadXml(packagePath)
				?? throw new UnreadableBookException("The book's package document is missing.");
			string packageDirectory = DirectoryOf(packagePath);

			XElement root = package.Root ?? throw new UnreadableBookException("The package document is empty.");
			XElement? metadata = Child(root, "metadata");
			string title = Clean(metadata is null ? null : Child(metadata, "title")?.Value);
			string? author = NullIfEmpty(Clean(metadata is null ? null : Child(metadata, "creator")?.Value));
			string? language = NullIfEmpty(Clean(metadata is null ? null : Child(metadata, "language")?.Value));

			Dictionary<string, ManifestItem> manifest = ReadManifest(root, packageDirectory);
			XElement spine = Child(root, "spine") ?? throw new UnreadableBookException("The book has no reading order (spine).");

			ManifestItem? navigation = manifest.Values.FirstOrDefault(item => HasToken(item.Properties, "nav"));
			HashSet<string> tableOfContentsPages = new(StringComparer.Ordinal);
			List<TocEntry> toc = [];
			if (navigation is not null)
			{
				tableOfContentsPages.Add(navigation.Path);
				toc = ReadNavigation(navigation.Path, tableOfContentsPages);
			}

			if (toc.Count == 0)
			{
				string? ncxId = spine.Attribute("toc")?.Value;
				ManifestItem? ncx = (ncxId is not null && manifest.TryGetValue(ncxId, out ManifestItem? named) ? named : null)
					?? manifest.Values.FirstOrDefault(item => item.MediaType == "application/x-dtbncx+xml");
				if (ncx is not null)
				{
					toc = ReadNcx(ncx.Path);
				}
			}

			foreach (string page in GuideTableOfContents(root, packageDirectory))
			{
				tableOfContentsPages.Add(page);
			}

			List<SpineDocument> documents = ReadSpine(spine, manifest, tableOfContentsPages);
			List<(string Title, List<RawParagraph> Paragraphs)> groups = Group(documents, toc, title);

			ImageCollector images = new(this);
			List<BookChapter> chapters = [];
			foreach ((string chapterTitle, List<RawParagraph> paragraphs) in groups)
			{
				List<BookParagraph> converted = [];
				foreach (RawParagraph paragraph in WithoutTitleEcho(paragraphs, chapterTitle))
				{
					BookParagraph? book = images.Convert(paragraph);
					if (book is not null)
					{
						converted.Add(book);
					}
				}

				chapters.Add(new BookChapter { Number = chapters.Count + 1, Title = chapterTitle, Paragraphs = converted });
			}

			return new ParsedBook
			{
				Title = title,
				Author = author,
				Language = language,
				Chapters = chapters,
				Images = images.Collected,
			};
		}

		// ---- The package ----

		private string FindPackagePath()
		{
			XDocument container = LoadXml("META-INF/container.xml")
				?? throw new UnreadableBookException("This is not an EPUB: META-INF/container.xml is missing.");

			string? path = container.Descendants()
				.Where(element => element.Name.LocalName == "rootfile")
				.Select(element => element.Attribute("full-path")?.Value)
				.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

			return Resolve("", path) ?? throw new UnreadableBookException("The EPUB does not say where its package document is.");
		}

		private static Dictionary<string, ManifestItem> ReadManifest(XElement root, string packageDirectory)
		{
			Dictionary<string, ManifestItem> manifest = new(StringComparer.Ordinal);
			XElement? element = Child(root, "manifest");
			if (element is null)
			{
				return manifest;
			}

			foreach (XElement item in element.Elements().Where(child => child.Name.LocalName == "item"))
			{
				string? id = item.Attribute("id")?.Value;
				string? path = Resolve(packageDirectory, item.Attribute("href")?.Value);
				if (id is null || path is null)
				{
					continue;
				}

				manifest.TryAdd(id, new ManifestItem(
					id,
					path,
					item.Attribute("media-type")?.Value.Trim().ToLowerInvariant() ?? string.Empty,
					item.Attribute("properties")?.Value ?? string.Empty));
			}

			return manifest;
		}

		/// <summary>The EPUB 2 guide's pointer at the book's own contents page.</summary>
		private static IEnumerable<string> GuideTableOfContents(XElement root, string packageDirectory)
		{
			XElement? guide = Child(root, "guide");
			if (guide is null)
			{
				yield break;
			}

			foreach (XElement reference in guide.Elements().Where(child => child.Name.LocalName == "reference"))
			{
				if (string.Equals(reference.Attribute("type")?.Value, "toc", StringComparison.OrdinalIgnoreCase)
					&& Resolve(packageDirectory, reference.Attribute("href")?.Value) is string path)
				{
					yield return path;
				}
			}
		}

		private List<SpineDocument> ReadSpine(XElement spine, Dictionary<string, ManifestItem> manifest, HashSet<string> skipped)
		{
			List<SpineDocument> documents = [];
			HashSet<string> seen = new(StringComparer.Ordinal);

			foreach (XElement itemRef in spine.Elements().Where(child => child.Name.LocalName == "itemref"))
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (string.Equals(itemRef.Attribute("linear")?.Value, "no", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				string? id = itemRef.Attribute("idref")?.Value;
				if (id is null || !manifest.TryGetValue(id, out ManifestItem? item) || skipped.Contains(item.Path) || !seen.Add(item.Path))
				{
					continue;
				}

				if (!item.MediaType.Contains("html", StringComparison.Ordinal))
				{
					continue;
				}

				HtmlDocument? document = LoadHtml(item.Path);
				if (document is null)
				{
					continue;
				}

				string directory = DirectoryOf(item.Path);
				documents.Add(new SpineDocument(item.Path, XhtmlParagraphs.Read(document, source => Resolve(directory, source))));
			}

			return documents;
		}

		// ---- The table of contents ----

		/// <summary>The EPUB 3 nav document's `toc` list, flattened in reading order; and its landmarks' contents page.</summary>
		private List<TocEntry> ReadNavigation(string path, HashSet<string> tableOfContentsPages)
		{
			HtmlDocument? document = LoadHtml(path);
			if (document is null)
			{
				return [];
			}

			string directory = DirectoryOf(path);
			List<HtmlNode> navs = [.. document.DocumentNode.Descendants("nav")];

			foreach (HtmlNode landmarks in navs.Where(nav => HasToken(nav.GetAttributeValue("epub:type", ""), "landmarks")))
			{
				foreach (HtmlNode link in landmarks.Descendants("a"))
				{
					if (HasToken(link.GetAttributeValue("epub:type", ""), "toc")
						&& Resolve(directory, link.GetAttributeValue("href", null)) is string page)
					{
						tableOfContentsPages.Add(page);
					}
				}
			}

			HtmlNode? toc = navs.FirstOrDefault(nav => HasToken(nav.GetAttributeValue("epub:type", ""), "toc"))
				?? navs.FirstOrDefault(nav => nav.Id == "toc");
			if (toc is null)
			{
				return [];
			}

			List<TocEntry> entries = [];
			foreach (HtmlNode link in toc.Descendants("a"))
			{
				if (Resolve(directory, link.GetAttributeValue("href", null)) is string target)
				{
					entries.Add(new TocEntry(Clean(HtmlEntity.DeEntitize(link.InnerText)), target));
				}
			}

			return entries;
		}

		/// <summary>The EPUB 2 NCX's navigation points, in document order — which nests sub-entries after their parent.</summary>
		private List<TocEntry> ReadNcx(string path)
		{
			XDocument? ncx = LoadXml(path);
			if (ncx is null)
			{
				return [];
			}

			string directory = DirectoryOf(path);
			List<TocEntry> entries = [];
			foreach (XElement point in ncx.Descendants().Where(element => element.Name.LocalName == "navPoint"))
			{
				string label = Clean(Child(point, "navLabel")?.Descendants().FirstOrDefault(element => element.Name.LocalName == "text")?.Value);
				if (Resolve(directory, Child(point, "content")?.Attribute("src")?.Value) is string target)
				{
					entries.Add(new TocEntry(label, target));
				}
			}

			return entries;
		}

		// ---- Chapters ----

		/// <summary>
		/// Spine documents into chapters, at the documents the table of contents points at. An
		/// entry that points backwards, or at a document an earlier entry already starts, is
		/// passed over: the second of two entries into one file is a section within it, and a
		/// file is never split.
		/// </summary>
		private static List<(string Title, List<RawParagraph> Paragraphs)> Group(
			List<SpineDocument> documents,
			List<TocEntry> toc,
			string bookTitle)
		{
			Dictionary<string, int> indexByPath = new(StringComparer.Ordinal);
			for (int index = 0; index < documents.Count; index++)
			{
				indexByPath.TryAdd(documents[index].Path, index);
			}

			List<(int Start, string Title)> starts = [];
			foreach (TocEntry entry in toc)
			{
				if (indexByPath.TryGetValue(entry.Path, out int index) && (starts.Count == 0 || index > starts[^1].Start))
				{
					starts.Add((index, entry.Title));
				}
			}

			List<(string Title, List<RawParagraph> Paragraphs)> groups = [];
			if (starts.Count == 0)
			{
				foreach (SpineDocument document in documents)
				{
					AddGroup(groups, string.Empty, document.Paragraphs);
				}

				return groups;
			}

			if (starts[0].Start > 0)
			{
				starts.Insert(0, (0, bookTitle));
			}

			for (int which = 0; which < starts.Count; which++)
			{
				int end = which + 1 < starts.Count ? starts[which + 1].Start : documents.Count;
				List<RawParagraph> paragraphs = [];
				for (int index = starts[which].Start; index < end; index++)
				{
					paragraphs.AddRange(documents[index].Paragraphs);
				}

				AddGroup(groups, starts[which].Title, paragraphs);
			}

			return groups;
		}

		/// <summary>A group with nothing in it is not a chapter. One with no title takes its first heading's.</summary>
		private static void AddGroup(List<(string Title, List<RawParagraph> Paragraphs)> groups, string title, List<RawParagraph> paragraphs)
		{
			if (paragraphs.Count == 0)
			{
				return;
			}

			if (title.Length == 0)
			{
				title = paragraphs.FirstOrDefault(paragraph => paragraph.IsHeading && !paragraph.HasImages)?.Text
					?? $"Part {groups.Count + 1}";
			}

			groups.Add((title, paragraphs));
		}

		/// <summary>
		/// Drops the leading text paragraphs that only spell the title out again. Each must carry
		/// on the title's words from where the last left off: for "Chapter 1: Baptism", the
		/// headings `CHAPTER 1` and `BAPTISM` go, and the section number `1` under them stays,
		/// because the title has no words left by then.
		/// </summary>
		internal static IEnumerable<RawParagraph> WithoutTitleEcho(List<RawParagraph> paragraphs, string title)
		{
			List<string> titleWords = WordsOf(title);
			int consumed = 0;
			int dropped = 0;

			foreach (RawParagraph paragraph in paragraphs)
			{
				if (paragraph.HasImages)
				{
					break;
				}

				List<string> words = WordsOf(paragraph.Text);
				bool continuesTitle = words.Count > 0
					&& consumed + words.Count <= titleWords.Count
					&& words.SequenceEqual(titleWords.Skip(consumed).Take(words.Count));
				if (!continuesTitle)
				{
					break;
				}

				consumed += words.Count;
				dropped++;
			}

			return paragraphs.Skip(dropped);
		}

		private static List<string> WordsOf(string text) =>
			[.. Word.Matches(text).Select(match => match.Value.ToLowerInvariant())];

		// ---- Pictures ----

		/// <summary>Looks each picture up once, keeps the ones the reader can show, and turns paragraphs into the book's own.</summary>
		private sealed class ImageCollector(EpubReader reader)
		{
			private readonly Dictionary<string, (string FileName, int Width, int Height)?> byPath = new(StringComparer.Ordinal);
			private readonly HashSet<string> stored = new(StringComparer.Ordinal);

			public List<ParsedImage> Collected { get; } = [];

			/// <summary>Null for a paragraph left with nothing, its pictures all unreadable.</summary>
			public BookParagraph? Convert(RawParagraph paragraph)
			{
				List<BookInline> inlines = [];
				foreach (object part in paragraph.Parts)
				{
					if (part is string text)
					{
						inlines.Add(BookInline.OfText(text));
					}
					else if (part is RawImage image && Find(image.Path) is { } found)
					{
						inlines.Add(BookInline.OfImage(new BookImage(found.FileName, found.Width, found.Height, image.Alt)));
					}
				}

				if (inlines.All(inline => inline.Image is null && string.IsNullOrWhiteSpace(inline.Text)))
				{
					return null;
				}

				return new BookParagraph(inlines);
			}

			private (string FileName, int Width, int Height)? Find(string path)
			{
				if (byPath.TryGetValue(path, out var known))
				{
					return known;
				}

				(string, int, int)? found = null;
				byte[]? bytes = reader.ReadEntry(path);
				if (bytes is not null && ImageSniffer.Sniff(bytes) is SniffedImage sniffed)
				{
					string fileName = BookImageName.For(bytes, sniffed.Extension);
					if (stored.Add(fileName))
					{
						Collected.Add(new ParsedImage
						{
							FileName = fileName,
							MediaType = BookImageName.MediaTypeOf(fileName),
							Bytes = bytes,
						});
					}

					found = (fileName, sniffed.Width, sniffed.Height);
				}

				byPath[path] = found;
				return found;
			}
		}

		// ---- The archive ----

		private byte[]? ReadEntry(string path)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (!TryFindEntry(path, out ZipArchiveEntry? entry))
			{
				return null;
			}

			if (entry.Length > MaximumEntryBytes)
			{
				throw new UnreadableBookException($"'{path}' is too large for a part of a book.");
			}

			using Stream stream = entry.Open();
			using MemoryStream buffer = new((int)Math.Max(0, Math.Min(entry.Length, MaximumEntryBytes)));
			byte[] chunk = new byte[81920];
			int read;
			while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
			{
				// Counted as it unpacks: the sizes an archive declares are the uploader's word.
				totalRead += read;
				if (buffer.Length + read > MaximumEntryBytes || totalRead > MaximumTotalBytes)
				{
					throw new UnreadableBookException("The book unpacks into far more than a book holds.");
				}

				buffer.Write(chunk, 0, read);
			}

			return buffer.ToArray();
		}

		/// <summary>Exactly as named, else ignoring case — archives made on Windows are not always consistent.</summary>
		private bool TryFindEntry(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ZipArchiveEntry? entry)
		{
			if (entries is null)
			{
				entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
				entriesIgnoringCase = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
				foreach (ZipArchiveEntry candidate in archive.Entries)
				{
					string name = candidate.FullName.Replace('\\', '/').TrimStart('/');
					entries.TryAdd(name, candidate);
					entriesIgnoringCase.TryAdd(name, candidate);
				}
			}

			return entries.TryGetValue(path, out entry) || entriesIgnoringCase!.TryGetValue(path, out entry);
		}

		private XDocument? LoadXml(string path)
		{
			byte[]? bytes = ReadEntry(path);
			if (bytes is null)
			{
				return null;
			}

			// No DTDs and nothing fetched: an NCX may name one, and it must not be followed.
			XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
			using MemoryStream stream = new(bytes);
			using XmlReader xml = XmlReader.Create(stream, settings);
			return XDocument.Load(xml);
		}

		private HtmlDocument? LoadHtml(string path)
		{
			byte[]? bytes = ReadEntry(path);
			if (bytes is null)
			{
				return null;
			}

			HtmlDocument document = new();
			using MemoryStream stream = new(bytes);
			document.Load(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
			return document;
		}

		// ---- Paths and text ----

		/// <summary>
		/// An <c>href</c> as a path inside the archive: relative to <paramref name="baseDirectory"/>,
		/// percent-decoded, without its fragment, with <c>.</c> and <c>..</c> worked out. Null for
		/// anything outside the archive — another site, a data URI.
		/// </summary>
		internal static string? Resolve(string baseDirectory, string? href)
		{
			if (string.IsNullOrWhiteSpace(href))
			{
				return null;
			}

			int cut = href.IndexOfAny(['#', '?']);
			string reference = (cut >= 0 ? href[..cut] : href).Trim();
			if (reference.Length == 0 || reference.Contains(':'))
			{
				// Empty, or a scheme: https:, data:, mailto:.
				return null;
			}

			reference = Uri.UnescapeDataString(reference).Replace('\\', '/');
			string combined = reference.StartsWith('/') || baseDirectory.Length == 0 ? reference : $"{baseDirectory}/{reference}";

			List<string> segments = [];
			foreach (string segment in combined.Split('/'))
			{
				if (segment is "" or ".")
				{
					continue;
				}

				if (segment == "..")
				{
					if (segments.Count > 0)
					{
						segments.RemoveAt(segments.Count - 1);
					}

					continue;
				}

				segments.Add(segment);
			}

			return segments.Count == 0 ? null : string.Join('/', segments);
		}

		private static string DirectoryOf(string path)
		{
			int slash = path.LastIndexOf('/');
			return slash < 0 ? string.Empty : path[..slash];
		}

		private static XElement? Child(XElement parent, string localName) =>
			parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName);

		private static bool HasToken(string list, string token) =>
			list.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(token, StringComparer.Ordinal);

		private static string Clean(string? text) => text is null ? string.Empty : Whitespace.Replace(text, " ").Trim();

		private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;
	}
}
