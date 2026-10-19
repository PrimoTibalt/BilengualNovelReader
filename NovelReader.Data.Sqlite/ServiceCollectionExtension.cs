using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Data.Sqlite;
using NovelReader.Domain.Monitoring;
using NovelReader.Domain.RealTimeReader.Accounts;
using NovelReader.Domain.RealTimeReader.Assistant;
using NovelReader.Domain.RealTimeReader.Translation;

namespace NovelReader.Data.Sqlite
{
	public static class ServiceCollectionExtension
	{
		/// <summary>
		/// Registers account storage and creates the database file if it is missing, so a
		/// fresh clone can sign up without a setup step.
		/// </summary>
		public static void AddSqliteAccounts(this IServiceCollection services, IConfiguration config)
		{
			string connectionString = config.GetConnectionString("AccountsConnectionString")
				?? "Data Source=novelreader-accounts.db";

			SqliteUserAccountRepository.EnsureCreated(connectionString);

			// One instance behind two interfaces: accounts and translation settings are the same
			// row, and splitting them into two connections to the same file buys nothing.
			SqliteUserAccountRepository repository = new(connectionString);

			services.AddSingleton<IUserAccountRepository>(repository);
			services.AddSingleton<IInviteStore>(repository);
			services.AddSingleton<ITranslationSettingsStore>(repository);
			services.AddSingleton<IAssistantSettingsStore>(repository);
			services.TryAddSingleton(TimeProvider.System);
			services.AddSingleton<AccountService>();
		}

		/// <summary>
		/// Registers the admin panel's store (D45), in its own file — by default beside the
		/// accounts database, as <c>novelreader-monitoring.db</c> — created if it is missing.
		/// </summary>
		public static void AddSqliteMonitoring(this IServiceCollection services, IConfiguration config)
		{
			string connectionString = config.GetConnectionString("MonitoringConnectionString")
				?? $"Data Source={Path.Combine(AccountsDirectory(config), "novelreader-monitoring.db")}";

			SqliteMonitoringStore.EnsureCreated(connectionString);
			services.AddSingleton<IMonitoringStore>(new SqliteMonitoringStore(connectionString));
		}

		/// <summary>
		/// The directory the accounts database lives in. Anything that has to survive exactly as
		/// long as the accounts do — the keys that protect the API keys stored in them (D35) —
		/// belongs beside it: a deploy that kept one and replaced the other would leave every
		/// stored key unreadable.
		/// </summary>
		public static string AccountsDirectory(IConfiguration config)
		{
			string connectionString = config.GetConnectionString("AccountsConnectionString")
				?? "Data Source=novelreader-accounts.db";

			string dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
			return Path.GetDirectoryName(Path.GetFullPath(dataSource)) ?? Directory.GetCurrentDirectory();
		}
	}
}
