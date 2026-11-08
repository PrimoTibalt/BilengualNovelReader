using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NovelReader.Domain;
using NovelReader.Domain.RealTimeReader.Books;
using NovelReader.Domain.RealTimeReader.Definitions;
using NovelReader.Domain.RealTimeReader.Reading;
using NovelReader.Domain.RealTimeReader.Review;
using NovelReader.Domain.RealTimeReader.User;
using NovelReader.Domain.RealTimeReader.Vocabulary;

namespace NovelReader.Data.Mongo
{
	public static class ServiceCollectionExtension
	{
		public static void AddMongoClient(this IServiceCollection services, IConfiguration config)
		{
      string? mongodbAtlusConnectionString = config.GetConnectionString("DefaultConnectionString");
      string? certificatePath = config["MongoOptions:X509Certificate"];
      string? certificatePass = config["MongoOptions:X509CertificatePass"];

			MongoClientSettings? settings = MongoClientSettings.FromConnectionString(mongodbAtlusConnectionString);

      // Atlas authenticates with a client certificate; a local Mongo (run.sh) has none, and
      // connects with the credentials in the connection string instead.
      if (!string.IsNullOrEmpty(certificatePath))
      {
        X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, certificatePass);
        settings.SslSettings = new() {
          ClientCertificates = [certificate]
        };
        settings.UseTls = true;
      }
			MongoClient client = new(settings);
			try
			{
				var result = client.GetDatabase("admin").RunCommand<BsonDocument>(new BsonDocument("ping", 1));
				Console.WriteLine("Pinged your deployment. You successfully connected to MongoDB!");
			}
			catch (Exception ex) { Console.WriteLine(ex); }

			try
			{
				// Idempotent, and cheap enough to run on every start.
				VocabularyMongoRepository.EnsureIndexesAsync(client).GetAwaiter().GetResult();
				ReadingProgressMongoStore.EnsureIndexesAsync(client).GetAwaiter().GetResult();
				ReviewCardMongoStore.EnsureIndexesAsync(client).GetAwaiter().GetResult();
				BookMongoStore.EnsureIndexesAsync(client).GetAwaiter().GetResult();
			}
			catch (Exception ex) { Console.WriteLine($"Could not ensure vocabulary indexes: {ex.Message}"); }

			services.AddSingleton(client);
		}

		public static void RegisterMongoImplementations(this IServiceCollection services)
		{
			// Bounded by entry count; every entry the prepared-chapter cache writes has Size 1.
			services.AddMemoryCache(options => options.SizeLimit = 64);

			services.AddSingleton<IReadingProgressStore, ReadingProgressMongoStore>();
			services.AddSingleton<INovelRepository, NovelMongoRepository>();
			services.AddSingleton<IVocabularyRepository, VocabularyMongoRepository>();
			services.AddSingleton<IDefinitionCache, DefinitionMongoCache>();
			services.AddSingleton<IPreparedChapterCache, PreparedChapterCache>();
			services.AddSingleton<IReviewCardStore, ReviewCardMongoStore>();
			services.AddSingleton<IBookStore, BookMongoStore>();
		}
	}
}
