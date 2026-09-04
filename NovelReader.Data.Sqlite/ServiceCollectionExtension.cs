using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NovelReader.Domain.RealTimeReader.Accounts;
using NovelReader.Domain.RealTimeReader.Translation;

namespace NovelReader.Data.Sqlite
{
	public static class ServiceCollectionExtension
	{
		/// <summary>
		/// Registers account storage and creates the database file if it is missing, so a
		/// fresh clone can sign up without a setup step.
		/// </summary>
		public static void AddSqliteAccounts(this IServiceCollection services, IConfiguration configuration)
		{
			string connectionString = configuration.GetConnectionString("AccountsConnectionString")
				?? "Data Source=novelreader-accounts.db";

			SqliteUserAccountRepository.EnsureCreated(connectionString);

			// One instance behind two interfaces: accounts and translation settings are the same
			// row, and splitting them into two connections to the same file buys nothing.
			SqliteUserAccountRepository repository = new(connectionString);

			services.AddSingleton<IUserAccountRepository>(repository);
			services.AddSingleton<ITranslationSettingsStore>(repository);
			services.AddSingleton<AccountService>();
		}
	}
}
