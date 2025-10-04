using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NovelReader.Domain;
using NovelReader.Domain.RealTimeReader.User;

namespace NovelReader.Data.Mongo
{
	public static class ServiceCollectionExtension
	{
		public static void AddMongoClient(this IServiceCollection services, IConfiguration configuration)
		{
			var settings = MongoClientSettings.FromConnectionString(configuration.GetConnectionString("DefaultConnectionString"));
			MongoClient client = new(settings);
			try
			{
				var result = client.GetDatabase("admin").RunCommand<BsonDocument>(new BsonDocument("ping", 1));
				Console.WriteLine("Pinged your deployment. You successfully connected to MongoDB!");
			}
			catch (Exception ex) { Console.WriteLine(ex); }

			services.AddSingleton<MongoClient>(client);
		}

		public static void RegisterMongoImplementations(this IServiceCollection services)
		{
			services.AddSingleton<IUserDataHandler, UserDataHandler>();
			services.AddSingleton<INovelRepository, NovelMongoRepository>();
		}
	}
}
