using NovelReader.Domain.RealTimeReader.Books;

namespace NovelReader.Books
{
	/// <summary>
	/// A book's pictures as files on the server, one directory per book:
	/// <c>{root}/{book slug}/{sha256}.{ext}</c> (D50).
	///
	/// On disk rather than in Mongo: the text is small, but one illustrated volume carries around
	/// 14 MB of pictures, which would eat a hosted database's quota a few books at a time, and
	/// files are what the server is best at handing out. The root sits beside the accounts
	/// database by default, so a deploy carries it over with the rest of what must survive one.
	///
	/// Names are checked against the slug and file-name patterns before they touch a path, so
	/// nothing here can be steered outside the root.
	/// </summary>
	internal sealed class FileSystemBookImageStore(string rootDirectory) : IBookImageStore
	{
		public async Task SaveAsync(string bookSlug, string fileName, byte[] bytes, CancellationToken cancellationToken = default)
		{
			string path = PathOf(bookSlug, fileName);
			if (File.Exists(path))
			{
				// Named by content: a file already there is this picture.
				return;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(path)!);

			// Written beside and moved into place, so a reader never gets half a picture.
			string temporary = $"{path}.{Guid.NewGuid():N}.part";
			try
			{
				await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
				File.Move(temporary, path, overwrite: true);
			}
			finally
			{
				if (File.Exists(temporary))
				{
					File.Delete(temporary);
				}
			}
		}

		public Stream? OpenRead(string bookSlug, string fileName)
		{
			if (!UploadedBookSlug.IsUploaded(bookSlug) || !BookImageName.IsValid(fileName))
			{
				return null;
			}

			string path = PathOf(bookSlug, fileName);
			return File.Exists(path)
				? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true)
				: null;
		}

		public Task DeleteBookAsync(string bookSlug, CancellationToken cancellationToken = default)
		{
			if (UploadedBookSlug.IsUploaded(bookSlug))
			{
				string directory = Path.Combine(rootDirectory, bookSlug);
				if (Directory.Exists(directory))
				{
					Directory.Delete(directory, recursive: true);
				}
			}

			return Task.CompletedTask;
		}

		private string PathOf(string bookSlug, string fileName)
		{
			if (!UploadedBookSlug.IsUploaded(bookSlug))
			{
				throw new ArgumentException($"'{bookSlug}' is not a book.", nameof(bookSlug));
			}

			if (!BookImageName.IsValid(fileName))
			{
				throw new ArgumentException($"'{fileName}' is not a picture's name.", nameof(fileName));
			}

			return Path.Combine(rootDirectory, bookSlug, fileName);
		}
	}
}
