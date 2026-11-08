using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using NovelReader.Domain.RealTimeReader.Books;

namespace NovelReader.Controllers
{
	/// <summary>
	/// The answer to an upload. <see cref="Error"/> is a code — the page owns the wording:
	/// <c>too-large</c>, <c>empty</c>, <c>unsupported</c>, <c>unreadable</c>, <c>no-content</c>,
	/// <c>too-many-books</c>, <c>rate-limited</c>.
	/// </summary>
	public record BookUploadResponse(bool Succeeded, string? Slug, string? Title, int ChapterCount, string? Error);

	/// <summary>
	/// Uploaded books over HTTP (D50): the upload itself, and the pictures the chapters point at.
	/// The hub carries everything else, as it does for scraped novels.
	///
	/// The upload is the file as the request body, not a form. A multipart form is buffered by
	/// ASP.NET to a temporary file once it passes 64 KB, and the file must never be written to the
	/// server's disk; the body is read straight into memory, parsed, and dropped.
	/// </summary>
	[ApiController]
	[Route(BookImageUrl.RoutePrefix)]
	[Authorize]
	public class BooksController(
		BookUploadService uploads,
		IBookStore books,
		IBookImageStore images,
		ILogger<BooksController> logger) : ControllerBase
	{
		/// <summary>The page names the file in the query; only its extension is ever read.</summary>
		[HttpPost]
		[EnableRateLimiting(AuthRateLimits.BookUpload)]
		[RequestSizeLimit(BookUploadService.MaximumUploadBytes)]
		public async Task<IActionResult> Upload([FromQuery] string? fileName, CancellationToken cancellationToken)
		{
			string userName = User.Identity!.Name!;

			if (Request.ContentLength > BookUploadService.MaximumUploadBytes)
			{
				return Refuse(StatusCodes.Status413PayloadTooLarge, "too-large");
			}

			using MemoryStream buffer = new();
			try
			{
				await Request.Body.CopyToAsync(buffer, cancellationToken);
			}
			catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
			{
				// No length was declared, and the body ran past the limit as it came.
				return Refuse(StatusCodes.Status413PayloadTooLarge, "too-large");
			}

			if (buffer.Length == 0)
			{
				return Refuse(StatusCodes.Status400BadRequest, "empty");
			}

			BookUploadResult result = await uploads.UploadAsync(userName, fileName ?? string.Empty, buffer, cancellationToken);
			if (!result.Succeeded)
			{
				logger.LogInformation("Book upload refused for {User}: {Failure} {Detail}", userName, result.Failure, result.Detail);
				return Refuse(StatusCodes.Status400BadRequest, result.Failure switch
				{
					BookUploadFailure.UnsupportedFormat => "unsupported",
					BookUploadFailure.NoContent => "no-content",
					BookUploadFailure.TooManyBooks => "too-many-books",
					_ => "unreadable",
				});
			}

			logger.LogInformation(
				"Book uploaded by {User}: {Slug} '{Title}', {Chapters} chapters, {Bytes} bytes",
				userName, result.Slug, result.Title, result.ChapterCount, buffer.Length);
			return Ok(new BookUploadResponse(true, result.Slug, result.Title, result.ChapterCount, null));
		}

		/// <summary>
		/// One picture of a book, to its owner only. Named by its content, so it never changes:
		/// the browser may keep it for a year without asking again. Private, because it is a
		/// reader's own book.
		/// </summary>
		[HttpGet("{slug}/images/{fileName}")]
		public async Task<IActionResult> Image(string slug, string fileName, CancellationToken cancellationToken)
		{
			if (!UploadedBookSlug.IsUploaded(slug) || !BookImageName.IsValid(fileName))
			{
				return NotFound();
			}

			// Another reader's book answers exactly as a missing one does.
			if (await books.GetAsync(User.Identity!.Name!, slug, cancellationToken) is null)
			{
				return NotFound();
			}

			Stream? picture = images.OpenRead(slug, fileName);
			if (picture is null)
			{
				return NotFound();
			}

			Response.Headers.CacheControl = "private, max-age=31536000, immutable";
			return File(picture, BookImageName.MediaTypeOf(fileName), lastModified: null,
				new EntityTagHeaderValue($"\"{Path.GetFileNameWithoutExtension(fileName)}\""), enableRangeProcessing: true);
		}

		private ObjectResult Refuse(int status, string error) =>
			StatusCode(status, new BookUploadResponse(false, null, null, 0, error));
	}
}
