using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NovelReader.Domain.RealTimeReader.Books
{
	/// <summary>
	/// The novel name an uploaded book goes by: <c>book--</c> and sixteen random hex digits.
	///
	/// It is a valid <see cref="Reading.NovelSlug"/>, so everything keyed by novel name — the
	/// bookmark, the library, saved words, the prepared-chapter cache — works for a book unchanged.
	/// The double dash is what tells it from the source site's slugs, which join a title's words
	/// with single dashes: the read path can route on the name alone, with no lookup, and a site
	/// novel can never be taken for someone's book. Random rather than derived from the title, so
	/// one reader's book cannot be found by guessing at another's (D50).
	/// </summary>
	public static partial class UploadedBookSlug
	{
		public const string Prefix = "book--";

		[GeneratedRegex("^book--[0-9a-f]{16}$")]
		private static partial Regex Pattern { get; }

		public static bool IsUploaded(string? novelName) =>
			novelName is not null && Pattern.IsMatch(novelName);

		public static string New() => Prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
	}

	/// <summary>
	/// A stored picture's file name: the SHA-256 of its bytes and the extension of its format.
	/// Named by content, a picture used twice in a book is stored once, and a URL never changes
	/// what it points at — which is what lets the browser cache it for good (D50).
	/// </summary>
	public static partial class BookImageName
	{
		[GeneratedRegex(@"^[0-9a-f]{64}\.(jpg|png|gif|webp)$")]
		private static partial Regex Pattern { get; }

		public static bool IsValid(string? fileName) => fileName is not null && Pattern.IsMatch(fileName);

		public static string For(byte[] bytes, string extension) =>
			$"{Convert.ToHexStringLower(SHA256.HashData(bytes))}.{extension}";

		public static string MediaTypeOf(string fileName) => Path.GetExtension(fileName) switch
		{
			".jpg" => "image/jpeg",
			".png" => "image/png",
			".gif" => "image/gif",
			".webp" => "image/webp",
			_ => "application/octet-stream",
		};
	}

	/// <summary>
	/// Where the page fetches a book's pictures, relative to the app's base (the page resolves it
	/// against <c>&lt;base href&gt;</c>, so it works under <c>/reader</c> too). The route that
	/// serves it, <c>BooksController</c>, is built from the same prefix.
	/// </summary>
	public static class BookImageUrl
	{
		public const string RoutePrefix = "books";

		public static string For(string bookSlug, string fileName) => $"{RoutePrefix}/{bookSlug}/images/{fileName}";
	}
}
