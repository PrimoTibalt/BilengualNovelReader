using NovelReader.Retrievers;
using NovelReader.Data.Mongo;
using NovelReader.Domain.RealTimeReader.Reading;

namespace NovelReader
{
	public class Program
	{
		public static void Main(string[] args)
		{
			var builder = WebApplication.CreateBuilder(args);

			builder.Services.RegisterHttpClientAndRetriever();
			builder.Services.AddMongoClient(builder.Configuration);
			builder.Services.RegisterMongoImplementations();
			builder.Services.AddSingleton<NextParagraphProcessor>();

			builder.Services.AddControllersWithViews();
			builder.Services.AddSignalR();

			var app = builder.Build();

			app.UseStaticFiles();
			app.UseHttpsRedirection();
			app.MapControllers().WithStaticAssets();
			app.MapHub<RealTimeReaderHub>("/signalr");

			app.Run();
		}
	}
}
