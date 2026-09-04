using Microsoft.Data.Sqlite;
using NovelReader.Domain.RealTimeReader.Accounts;
using NovelReader.Domain.RealTimeReader.Translation;

namespace NovelReader.Data.Sqlite
{
	/// <summary>
	/// Accounts in a local SQLite file. One table, no ORM — this stores three columns and a
	/// migration framework would be more moving parts than the thing it manages.
	/// </summary>
	internal sealed class SqliteUserAccountRepository(string connectionString)
		: IUserAccountRepository, ITranslationSettingsStore
	{
		/// <summary>SQLite's generic constraint-violation result code.</summary>
		private const int SqliteConstraint = 19;

		public async Task<AccountResult> CreateAsync(string userName, string passwordHash, CancellationToken cancellationToken = default)
		{
			await using SqliteConnection connection = new(connectionString);
			await connection.OpenAsync(cancellationToken);

			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				INSERT INTO accounts (user_name, password_hash, created_at)
				VALUES ($userName, $passwordHash, $createdAt);
				""";
			command.Parameters.AddWithValue("$userName", userName);
			command.Parameters.AddWithValue("$passwordHash", passwordHash);
			command.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));

			try
			{
				await command.ExecuteNonQueryAsync(cancellationToken);
				return AccountResult.Success(userName);
			}
			catch (SqliteException exception) when (exception.SqliteErrorCode == SqliteConstraint)
			{
				// The unique index is the only thing that can settle this: checking first and
				// inserting second leaves a window where two sign-ups both see the name free.
				return AccountResult.Failed(AccountFailure.UserNameTaken);
			}
		}

		public async Task<UserAccount?> FindAsync(string userName, CancellationToken cancellationToken = default)
		{
			await using SqliteConnection connection = new(connectionString);
			await connection.OpenAsync(cancellationToken);

			await using SqliteCommand command = connection.CreateCommand();
			// The column collates NOCASE, so this matches whatever casing was registered.
			command.CommandText = """
				SELECT user_name, password_hash, created_at
				FROM accounts
				WHERE user_name = $userName
				LIMIT 1;
				""";
			command.Parameters.AddWithValue("$userName", userName);

			await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			if (!await reader.ReadAsync(cancellationToken))
			{
				return null;
			}

			return new UserAccount
			{
				UserName = reader.GetString(0),
				PasswordHash = reader.GetString(1),
				CreatedAtUtc = DateTime.TryParse(reader.GetString(2), out DateTime created)
					? created
					: DateTime.UnixEpoch
			};
		}

		/// <summary>
		/// This reader's translation settings, or null when they have never set them up — which
		/// is what tells the page to ask for them instead of translating (D31). Both columns are
		/// nullable and are only ever written together, so a half-configured row cannot happen.
		/// </summary>
		public async Task<TranslationSettings?> GetAsync(string userName, CancellationToken cancellationToken = default)
		{
			await using SqliteConnection connection = new(connectionString);
			await connection.OpenAsync(cancellationToken);

			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				SELECT translation_email, translation_language
				FROM accounts
				WHERE user_name = $userName
				LIMIT 1;
				""";
			command.Parameters.AddWithValue("$userName", userName);

			await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0) || reader.IsDBNull(1))
			{
				return null;
			}

			string email = reader.GetString(0);
			string language = reader.GetString(1);

			return email.Length == 0 || language.Length == 0 ? null : new TranslationSettings(email, language);
		}

		public async Task SaveAsync(string userName, TranslationSettings settings, CancellationToken cancellationToken = default)
		{
			await using SqliteConnection connection = new(connectionString);
			await connection.OpenAsync(cancellationToken);

			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				UPDATE accounts
				SET translation_email = $email, translation_language = $language
				WHERE user_name = $userName;
				""";
			command.Parameters.AddWithValue("$email", settings.Email);
			command.Parameters.AddWithValue("$language", settings.TargetLanguage);
			command.Parameters.AddWithValue("$userName", userName);

			await command.ExecuteNonQueryAsync(cancellationToken);
		}

		/// <summary>
		/// Creates the table if it is not there. <c>COLLATE NOCASE</c> on the primary key is
		/// what makes "Anton" and "anton" the same account rather than two.
		/// </summary>
		internal static void EnsureCreated(string connectionString)
		{
			using SqliteConnection connection = new(connectionString);
			connection.Open();

			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				PRAGMA journal_mode = WAL;

				CREATE TABLE IF NOT EXISTS accounts (
					user_name     TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
					password_hash TEXT NOT NULL,
					created_at    TEXT NOT NULL
				);
				""";
			command.ExecuteNonQuery();

			// Added after the table shipped, so existing databases need them bolted on. SQLite
			// has no ADD COLUMN IF NOT EXISTS, and adding one twice is an error rather than a
			// no-op, so the column list is what decides whether to run it.
			EnsureColumn(connection, "translation_email", "TEXT NULL");
			EnsureColumn(connection, "translation_language", "TEXT NULL");
		}

		private static void EnsureColumn(SqliteConnection connection, string columnName, string definition)
		{
			using SqliteCommand exists = connection.CreateCommand();
			exists.CommandText = "SELECT COUNT(*) FROM pragma_table_info('accounts') WHERE name = $name;";
			exists.Parameters.AddWithValue("$name", columnName);

			if (Convert.ToInt64(exists.ExecuteScalar()) > 0)
			{
				return;
			}

			using SqliteCommand add = connection.CreateCommand();
			// The names are literals from the call sites above, never user input.
			add.CommandText = $"ALTER TABLE accounts ADD COLUMN {columnName} {definition};";
			add.ExecuteNonQuery();
		}
	}
}
