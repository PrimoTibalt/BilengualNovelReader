using Microsoft.Data.Sqlite;
using NovelReader.Domain.RealTimeReader.Accounts;
using NovelReader.Domain.RealTimeReader.Assistant;
using NovelReader.Domain.RealTimeReader.Translation;

namespace NovelReader.Data.Sqlite
{
	/// <summary>
	/// Accounts in a local SQLite file. One table, no ORM — this stores three columns and a
	/// migration framework would be more moving parts than the thing it manages.
	/// </summary>
	internal sealed class SqliteUserAccountRepository(string connectionString)
		: IUserAccountRepository, IInviteStore, ITranslationSettingsStore, IAssistantSettingsStore
	{
		/// <summary>SQLite's generic constraint-violation result code.</summary>
		private const int SqliteConstraint = 19;

		public async Task<AccountResult> CreateAsync(
			string userName,
			string passwordHash,
			string inviteCodeHash,
			DateTimeOffset now,
			CancellationToken cancellationToken = default)
		{
			await using SqliteConnection connection = new(connectionString);
			await connection.OpenAsync(cancellationToken);

			// One transaction, so the invite is spent exactly when the account appears (D42).
			await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

			await using SqliteCommand spend = connection.CreateCommand();
			spend.Transaction = transaction;
			// A conditional UPDATE rather than read-then-write, so two sign-ups racing for one
			// code cannot both see it unspent.
			spend.CommandText = """
				UPDATE invites
				SET used_by = $userName, used_at = $now
				WHERE code_hash = $codeHash AND used_at IS NULL AND expires_at > $now;
				""";
			spend.Parameters.AddWithValue("$userName", userName);
			spend.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
			spend.Parameters.AddWithValue("$codeHash", inviteCodeHash);

			if (await spend.ExecuteNonQueryAsync(cancellationToken) != 1)
			{
				await transaction.RollbackAsync(cancellationToken);
				return AccountResult.Failed(AccountFailure.InviteInvalid);
			}

			await using SqliteCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = """
				INSERT INTO accounts (user_name, password_hash, created_at)
				VALUES ($userName, $passwordHash, $createdAt);
				""";
			command.Parameters.AddWithValue("$userName", userName);
			command.Parameters.AddWithValue("$passwordHash", passwordHash);
			command.Parameters.AddWithValue("$createdAt", now.UtcDateTime.ToString("O"));

			try
			{
				await command.ExecuteNonQueryAsync(cancellationToken);
			}
			catch (SqliteException exception) when (exception.SqliteErrorCode == SqliteConstraint)
			{
				// The unique index is the only thing that can settle this: checking first and
				// inserting second leaves a window where two sign-ups both see the name free.
				// Rolled back, the invite is unspent and can be tried with another name.
				await transaction.RollbackAsync(cancellationToken);
				return AccountResult.Failed(AccountFailure.UserNameTaken);
			}

			await transaction.CommitAsync(cancellationToken);
			return AccountResult.Success(userName);
		}

		public async Task CreateInviteAsync(string codeHash, DateTimeOffset createdAt, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
		{
			await using SqliteConnection connection = new(connectionString);
			await connection.OpenAsync(cancellationToken);

			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				INSERT INTO invites (code_hash, created_at, expires_at)
				VALUES ($codeHash, $createdAt, $expiresAt);
				""";
			command.Parameters.AddWithValue("$codeHash", codeHash);
			command.Parameters.AddWithValue("$createdAt", createdAt.ToUnixTimeSeconds());
			command.Parameters.AddWithValue("$expiresAt", expiresAt.ToUnixTimeSeconds());

			await command.ExecuteNonQueryAsync(cancellationToken);
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

		public async Task<bool> UpdatePasswordHashAsync(string userName, string passwordHash, CancellationToken cancellationToken = default)
		{
			await using SqliteConnection connection = new(connectionString);
			await connection.OpenAsync(cancellationToken);

			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				UPDATE accounts
				SET password_hash = $passwordHash
				WHERE user_name = $userName;
				""";
			command.Parameters.AddWithValue("$passwordHash", passwordHash);
			command.Parameters.AddWithValue("$userName", userName);

			return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
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
		/// This reader's AI settings, the key still protected (D35). Null when any of the three
		/// is missing — they are written and cleared together, so that only means "not set".
		/// </summary>
		async Task<StoredAssistantSettings?> IAssistantSettingsStore.GetAsync(string userName, CancellationToken cancellationToken)
		{
			await using SqliteConnection connection = new(connectionString);
			await connection.OpenAsync(cancellationToken);

			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				SELECT assistant_service, assistant_model, assistant_key
				FROM accounts
				WHERE user_name = $userName
				LIMIT 1;
				""";
			command.Parameters.AddWithValue("$userName", userName);

			await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
			{
				return null;
			}

			return new StoredAssistantSettings(reader.GetString(0), reader.GetString(1), reader.GetString(2));
		}

		async Task IAssistantSettingsStore.SaveAsync(string userName, StoredAssistantSettings settings, CancellationToken cancellationToken)
		{
			await using SqliteConnection connection = new(connectionString);
			await connection.OpenAsync(cancellationToken);

			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				UPDATE accounts
				SET assistant_service = $service, assistant_model = $model, assistant_key = $key
				WHERE user_name = $userName;
				""";
			command.Parameters.AddWithValue("$service", settings.ServiceId);
			command.Parameters.AddWithValue("$model", settings.Model);
			command.Parameters.AddWithValue("$key", settings.ProtectedApiKey);
			command.Parameters.AddWithValue("$userName", userName);

			await command.ExecuteNonQueryAsync(cancellationToken);
		}

		/// <summary>Clears all three, so a removed key is gone rather than merely unused.</summary>
		public async Task RemoveAsync(string userName, CancellationToken cancellationToken = default)
		{
			await using SqliteConnection connection = new(connectionString);
			await connection.OpenAsync(cancellationToken);

			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				UPDATE accounts
				SET assistant_service = NULL, assistant_model = NULL, assistant_key = NULL
				WHERE user_name = $userName;
				""";
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

				-- Sign-up invitations (D42). Only the code's SHA-256 is kept; times are Unix seconds.
				-- Spent rows stay, as the record of who came in on which invite.
				CREATE TABLE IF NOT EXISTS invites (
					code_hash  TEXT    NOT NULL PRIMARY KEY,
					created_at INTEGER NOT NULL,
					expires_at INTEGER NOT NULL,
					used_by    TEXT    NULL,
					used_at    INTEGER NULL
				);
				""";
			command.ExecuteNonQuery();

			// Added after the table shipped, so existing databases need them bolted on. SQLite
			// has no ADD COLUMN IF NOT EXISTS, and adding one twice is an error rather than a
			// no-op, so the column list is what decides whether to run it.
			EnsureColumn(connection, "translation_email", "TEXT NULL");
			EnsureColumn(connection, "translation_language", "TEXT NULL");
			EnsureColumn(connection, "assistant_service", "TEXT NULL");
			EnsureColumn(connection, "assistant_model", "TEXT NULL");
			EnsureColumn(connection, "assistant_key", "TEXT NULL");
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
