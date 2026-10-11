using NovelReader.Data.Sqlite;
using NovelReader.Domain.RealTimeReader.Accounts;
using NovelReader.Domain.RealTimeReader.Assistant;

namespace NovelReader.Tests
{
	/// <summary>
	/// Against a real SQLite file, because the guarantee being tested — a unique, case-
	/// insensitive username — is the database's, not the code's.
	/// </summary>
	public sealed class SqliteUserAccountRepositoryTests : IDisposable
	{
		private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"novelreader-test-{Guid.NewGuid():N}.db");
		private readonly AccountService service;
		private readonly SqliteUserAccountRepository repository;

		public SqliteUserAccountRepositoryTests()
		{
			string connectionString = $"Data Source={databasePath}";
			SqliteUserAccountRepository.EnsureCreated(connectionString);
			repository = new SqliteUserAccountRepository(connectionString);
			service = new AccountService(repository, new SignInThrottle(TimeProvider.System));
		}

		public void Dispose()
		{
			Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
			foreach (string suffix in new[] { "", "-wal", "-shm" })
			{
				File.Delete(databasePath + suffix);
			}
		}

		[Fact]
		public async Task An_account_survives_a_round_trip()
		{
			Assert.True((await service.SignUpAsync("anton", "a-good-password")).Succeeded);

			AccountResult signedIn = await service.SignInAsync("anton", "a-good-password");

			Assert.True(signedIn.Succeeded);
			Assert.Equal("anton", signedIn.UserName);
		}

		[Fact]
		public async Task A_password_change_is_stored()
		{
			await service.SignUpAsync("Anton", "a-good-password");

			// The column collates NOCASE, so the update finds the row whatever the casing.
			Assert.True((await service.ChangePasswordAsync("anton", "a-good-password", "a-new-password")).Succeeded);

			Assert.True((await service.SignInAsync("Anton", "a-new-password")).Succeeded);
			Assert.False((await service.SignInAsync("Anton", "a-good-password")).Succeeded);
			Assert.False(await repository.UpdatePasswordHashAsync("nobody", "x"));
		}

		[Fact]
		public async Task The_database_refuses_a_duplicate_username()
		{
			await service.SignUpAsync("anton", "a-good-password");

			AccountResult second = await service.SignUpAsync("anton", "a-different-password");

			Assert.False(second.Succeeded);
			Assert.Equal(AccountFailure.UserNameTaken, second.Failure);
		}

		[Fact]
		public async Task Usernames_are_unique_regardless_of_case()
		{
			await service.SignUpAsync("Anton", "a-good-password");

			AccountResult second = await service.SignUpAsync("ANTON", "a-good-password");

			Assert.Equal(AccountFailure.UserNameTaken, second.Failure);
		}

		[Fact]
		public async Task The_stored_spelling_is_what_comes_back()
		{
			await service.SignUpAsync("Anton", "a-good-password");

			AccountResult signedIn = await service.SignInAsync("aNtOn", "a-good-password");

			Assert.True(signedIn.Succeeded);
			Assert.Equal("Anton", signedIn.UserName);
		}

		[Fact]
		public async Task An_unknown_user_cannot_sign_in()
		{
			AccountResult result = await service.SignInAsync("nobody", "a-good-password");

			Assert.False(result.Succeeded);
			Assert.Equal(AccountFailure.UnknownUserOrPassword, result.Failure);
		}

		[Fact]
		public async Task The_stored_password_is_not_the_password()
		{
			await service.SignUpAsync("anton", "a-good-password");

			string contents = await File.ReadAllTextAsync(databasePath);

			Assert.DoesNotContain("a-good-password", contents);
		}
	
		[Fact]
		public async Task Ai_settings_round_trip_and_are_cleared_together()
		{
			await service.SignUpAsync("anton", "a-good-password");
			IAssistantSettingsStore store = repository;

			Assert.Null(await store.GetAsync("anton"));

			await store.SaveAsync("anton", new StoredAssistantSettings("digitalocean", "glm-5.3", "protected-key"));
			// Same account whatever the casing, as for everything else on this row.
			Assert.Equal(new StoredAssistantSettings("digitalocean", "glm-5.3", "protected-key"), await store.GetAsync("ANTON"));

			await store.RemoveAsync("anton");
			Assert.Null(await store.GetAsync("anton"));
		}
	}
}
