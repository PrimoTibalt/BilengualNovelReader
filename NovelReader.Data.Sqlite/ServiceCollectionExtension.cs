using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NovelReader.Domain.RealTimeReader.Accounts;

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

			services.AddSingleton<IUserAccountRepository>(_ => new SqliteUserAccountRepository(connectionString));
			services.AddSingleton<AccountService>();
		}
	}
}
