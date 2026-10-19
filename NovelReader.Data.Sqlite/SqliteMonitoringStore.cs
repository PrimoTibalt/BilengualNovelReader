using Microsoft.Data.Sqlite;
using NovelReader.Domain.Monitoring;

namespace NovelReader.Data.Sqlite
{
	/// <summary>
	/// The admin panel's records in their own SQLite file (D45), next to the accounts but not in
	/// them: this file grows and is pruned, the accounts must never be. Times are Unix
	/// milliseconds; a timing row's minute is Unix seconds of the minute's start.
	/// </summary>
	internal sealed class SqliteMonitoringStore(string connectionString) : IMonitoringStore
	{
		/// <summary>An exception's text is cut to this; a stack trace past it is noise.</summary>
		private const int MaxExceptionLength = 20_000;

		/// <summary>However young they are, only this many errors are kept — a fault in a loop must not fill the disk.</summary>
		private const int MaxErrors = 2000;

		public async Task RecordAuthEventAsync(AuthEvent authEvent, CancellationToken cancellationToken = default)
		{
			await using SqliteConnection connection = await OpenAsync(cancellationToken);
			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				INSERT INTO auth_events (at, outcome, user_name, address, detail)
				VALUES ($at, $outcome, $userName, $address, $detail);
				""";
			command.Parameters.AddWithValue("$at", Milliseconds(authEvent.AtUtc));
			command.Parameters.AddWithValue("$outcome", authEvent.Outcome.ToString());
			command.Parameters.AddWithValue("$userName", (object?)authEvent.UserName ?? DBNull.Value);
			command.Parameters.AddWithValue("$address", (object?)authEvent.Address ?? DBNull.Value);
			command.Parameters.AddWithValue("$detail", (object?)authEvent.Detail ?? DBNull.Value);
			await command.ExecuteNonQueryAsync(cancellationToken);
		}

		public async Task<IReadOnlyList<AuthEvent>> GetAuthEventsAsync(int limit, CancellationToken cancellationToken = default)
		{
			await using SqliteConnection connection = await OpenAsync(cancellationToken);
			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				SELECT at, outcome, user_name, address, detail
				FROM auth_events
				ORDER BY at DESC, id DESC
				LIMIT $limit;
				""";
			command.Parameters.AddWithValue("$limit", limit);

			List<AuthEvent> events = [];
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			while (await reader.ReadAsync(cancellationToken))
			{
				if (!Enum.TryParse(reader.GetString(1), out AuthOutcome outcome))
				{
					continue;
				}

				events.Add(new AuthEvent(
					FromMilliseconds(reader.GetInt64(0)),
					outcome,
					reader.IsDBNull(2) ? null : reader.GetString(2),
					reader.IsDBNull(3) ? null : reader.GetString(3),
					reader.IsDBNull(4) ? null : reader.GetString(4)));
			}
			return events;
		}

		public async Task<IReadOnlyDictionary<AuthOutcome, int>> CountAuthEventsAsync(DateTime sinceUtc, CancellationToken cancellationToken = default)
		{
			await using SqliteConnection connection = await OpenAsync(cancellationToken);
			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				SELECT outcome, COUNT(*)
				FROM auth_events
				WHERE at >= $since
				GROUP BY outcome;
				""";
			command.Parameters.AddWithValue("$since", Milliseconds(sinceUtc));

			Dictionary<AuthOutcome, int> counts = [];
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			while (await reader.ReadAsync(cancellationToken))
			{
				if (Enum.TryParse(reader.GetString(0), out AuthOutcome outcome))
				{
					counts[outcome] = reader.GetInt32(1);
				}
			}
			return counts;
		}

		public async Task SaveErrorsAsync(IReadOnlyList<ErrorEntry> errors, CancellationToken cancellationToken = default)
		{
			if (errors.Count == 0)
			{
				return;
			}

			await using SqliteConnection connection = await OpenAsync(cancellationToken);
			await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
			foreach (ErrorEntry error in errors)
			{
				await using SqliteCommand command = connection.CreateCommand();
				command.Transaction = transaction;
				command.CommandText = """
					INSERT INTO errors (at, level, category, message, exception)
					VALUES ($at, $level, $category, $message, $exception);
					""";
				command.Parameters.AddWithValue("$at", Milliseconds(error.AtUtc));
				command.Parameters.AddWithValue("$level", error.Level);
				command.Parameters.AddWithValue("$category", error.Category);
				command.Parameters.AddWithValue("$message", error.Message);
				command.Parameters.AddWithValue("$exception", error.Exception is null
					? DBNull.Value
					: error.Exception.Length > MaxExceptionLength ? error.Exception[..MaxExceptionLength] : error.Exception);
				await command.ExecuteNonQueryAsync(cancellationToken);
			}
			await transaction.CommitAsync(cancellationToken);
		}

		public async Task<IReadOnlyList<ErrorEntry>> GetErrorsAsync(int limit, CancellationToken cancellationToken = default)
		{
			await using SqliteConnection connection = await OpenAsync(cancellationToken);
			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				SELECT at, level, category, message, exception
				FROM errors
				ORDER BY at DESC, id DESC
				LIMIT $limit;
				""";
			command.Parameters.AddWithValue("$limit", limit);

			List<ErrorEntry> errors = [];
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			while (await reader.ReadAsync(cancellationToken))
			{
				errors.Add(new ErrorEntry(
					FromMilliseconds(reader.GetInt64(0)),
					reader.GetString(1),
					reader.GetString(2),
					reader.GetString(3),
					reader.IsDBNull(4) ? null : reader.GetString(4)));
			}
			return errors;
		}

		public async Task SaveTimingsAsync(IReadOnlyList<TimingSample> samples, CancellationToken cancellationToken = default)
		{
			if (samples.Count == 0)
			{
				return;
			}

			await using SqliteConnection connection = await OpenAsync(cancellationToken);
			await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
			foreach (TimingSample sample in samples)
			{
				long minute = Seconds(sample.MinuteUtc);

				// Read, add, write — the histogram is text, so SQL cannot add it up itself.
				LatencyHistogram total = sample.Histogram.Clone();
				await using (SqliteCommand read = connection.CreateCommand())
				{
					read.Transaction = transaction;
					read.CommandText = "SELECT counts FROM response_times WHERE minute = $minute AND operation = $operation;";
					read.Parameters.AddWithValue("$minute", minute);
					read.Parameters.AddWithValue("$operation", sample.Operation);
					if (await read.ExecuteScalarAsync(cancellationToken) is string stored)
					{
						total.Merge(LatencyHistogram.Parse(stored));
					}
				}

				await using SqliteCommand write = connection.CreateCommand();
				write.Transaction = transaction;
				write.CommandText = """
					INSERT INTO response_times (minute, operation, counts)
					VALUES ($minute, $operation, $counts)
					ON CONFLICT (minute, operation) DO UPDATE SET counts = excluded.counts;
					""";
				write.Parameters.AddWithValue("$minute", minute);
				write.Parameters.AddWithValue("$operation", sample.Operation);
				write.Parameters.AddWithValue("$counts", total.ToText());
				await write.ExecuteNonQueryAsync(cancellationToken);
			}
			await transaction.CommitAsync(cancellationToken);
		}

		public async Task<IReadOnlyList<TimingSample>> GetTimingsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
		{
			await using SqliteConnection connection = await OpenAsync(cancellationToken);
			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				SELECT minute, operation, counts
				FROM response_times
				WHERE minute >= $from AND minute < $to;
				""";
			command.Parameters.AddWithValue("$from", Seconds(fromUtc));
			command.Parameters.AddWithValue("$to", Seconds(toUtc));

			List<TimingSample> samples = [];
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
			while (await reader.ReadAsync(cancellationToken))
			{
				samples.Add(new TimingSample(
					DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)).UtcDateTime,
					reader.GetString(1),
					LatencyHistogram.Parse(reader.GetString(2))));
			}
			return samples;
		}

		public async Task PruneAsync(DateTime timingsBeforeUtc, DateTime eventsBeforeUtc, DateTime errorsBeforeUtc, CancellationToken cancellationToken = default)
		{
			await using SqliteConnection connection = await OpenAsync(cancellationToken);
			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				DELETE FROM response_times WHERE minute < $timingsBefore;
				DELETE FROM auth_events WHERE at < $eventsBefore;
				DELETE FROM errors WHERE at < $errorsBefore
					OR id NOT IN (SELECT id FROM errors ORDER BY at DESC, id DESC LIMIT $maxErrors);
				""";
			command.Parameters.AddWithValue("$timingsBefore", Seconds(timingsBeforeUtc));
			command.Parameters.AddWithValue("$eventsBefore", Milliseconds(eventsBeforeUtc));
			command.Parameters.AddWithValue("$errorsBefore", Milliseconds(errorsBeforeUtc));
			command.Parameters.AddWithValue("$maxErrors", MaxErrors);
			await command.ExecuteNonQueryAsync(cancellationToken);
		}

		internal static void EnsureCreated(string connectionString)
		{
			using SqliteConnection connection = new(connectionString);
			connection.Open();

			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				PRAGMA journal_mode = WAL;

				CREATE TABLE IF NOT EXISTS auth_events (
					id        INTEGER PRIMARY KEY AUTOINCREMENT,
					at        INTEGER NOT NULL,
					outcome   TEXT    NOT NULL,
					user_name TEXT    NULL,
					address   TEXT    NULL,
					detail    TEXT    NULL
				);
				CREATE INDEX IF NOT EXISTS auth_events_at ON auth_events (at);

				CREATE TABLE IF NOT EXISTS errors (
					id        INTEGER PRIMARY KEY AUTOINCREMENT,
					at        INTEGER NOT NULL,
					level     TEXT    NOT NULL,
					category  TEXT    NOT NULL,
					message   TEXT    NOT NULL,
					exception TEXT    NULL
				);
				CREATE INDEX IF NOT EXISTS errors_at ON errors (at);

				-- One row per operation per minute that had any traffic; counts is LatencyHistogram.ToText().
				CREATE TABLE IF NOT EXISTS response_times (
					minute    INTEGER NOT NULL,
					operation TEXT    NOT NULL,
					counts    TEXT    NOT NULL,
					PRIMARY KEY (minute, operation)
				) WITHOUT ROWID;
				""";
			command.ExecuteNonQuery();
		}

		private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
		{
			SqliteConnection connection = new(connectionString);
			await connection.OpenAsync(cancellationToken);
			return connection;
		}

		private static long Seconds(DateTime utc) =>
			new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

		private static long Milliseconds(DateTime utc) =>
			new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

		private static DateTime FromMilliseconds(long milliseconds) =>
			DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
	}
}
