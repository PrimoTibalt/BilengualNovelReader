using NovelReader.Data.Sqlite;
using NovelReader.Domain.RealTimeReader.Accounts;

namespace NovelReader
{
	/// <summary>
	/// <c>NovelReader invite</c>: prints a new sign-up invite code and exits (D42). Run it on
	/// the server, from the directory the app runs in and with the same environment, so it opens
	/// the same accounts database — the path it used is printed with the code.
	///
	/// Only the code goes to standard output, so <c>NovelReader invite | wl-copy</c> works.
	/// </summary>
	internal static class InviteCommand
	{
		public static bool IsInvoked(string[] args) => args is ["invite", ..];

		public static void Run(string[] args)
		{
			// The app's own configuration — appsettings, environment, command line — without
			// starting the app, so Mongo and the rest are never touched.
			IConfiguration configuration = WebApplication.CreateBuilder(args[1..]).Configuration;

			ServiceCollection services = new();
			services.AddSqliteAccounts(configuration);

			using ServiceProvider provider = services.BuildServiceProvider();
			(string code, DateTimeOffset expiresAt) = provider.GetRequiredService<AccountService>()
				.CreateInviteAsync().GetAwaiter().GetResult();

			Console.WriteLine(code);
			Console.Error.WriteLine($"Single use, valid until {expiresAt:yyyy-MM-dd HH:mm} UTC.");
			Console.Error.WriteLine($"Accounts database: {ServiceCollectionExtension.AccountsDirectory(configuration)}");
		}
	}
}
