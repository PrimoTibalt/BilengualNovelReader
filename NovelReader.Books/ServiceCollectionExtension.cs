using Microsoft.Extensions.DependencyInjection;
using NovelReader.Domain.RealTimeReader.Books;

namespace NovelReader.Books
{
	public static class ServiceCollectionExtension
	{
		/// <summary>
		/// The book formats a reader can upload, and where the pictures are kept (D50). A new
		/// format is one more <see cref="IBookParser"/> registered here.
		/// </summary>
		public static void AddBooks(this IServiceCollection services, string imageDirectory)
		{
			services.AddSingleton<IBookParser, EpubParser>();
			services.AddSingleton<IBookImageStore>(new FileSystemBookImageStore(imageDirectory));
		}
	}
}
