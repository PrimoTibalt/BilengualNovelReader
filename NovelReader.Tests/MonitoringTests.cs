using NovelReader.Data.Sqlite;
using NovelReader.Domain.Monitoring;

namespace NovelReader.Tests
{
	public class LatencyHistogramTests
	{
		[Fact]
		public void Percentiles_come_back_within_the_bucket_error()
		{
			LatencyHistogram histogram = new();
			for (int milliseconds = 1; milliseconds <= 100; milliseconds++)
			{
				histogram.Add(milliseconds);
			}

			// True p50 is 50 ms, p95 is 95 ms; a bucket is 20% wide, read back at its middle.
			Assert.InRange(histogram.Percentile(0.5)!.Value, 45, 55);
			Assert.InRange(histogram.Percentile(0.95)!.Value, 85.5, 104.5);
		}

		[Fact]
		public void An_empty_histogram_has_no_percentile()
		{
			Assert.Null(new LatencyHistogram().Percentile(0.5));
		}

		[Fact]
		public void Text_round_trips_and_extremes_are_kept()
		{
			LatencyHistogram histogram = new();
			histogram.Add(0.01);
			histogram.Add(12);
			histogram.Add(12);
			histogram.Add(10_000_000);

			LatencyHistogram read = LatencyHistogram.Parse(histogram.ToText());

			Assert.Equal(4, read.Count);
			Assert.Equal(histogram.ToText(), read.ToText());
			Assert.Equal(histogram.Percentile(0.5), read.Percentile(0.5));
			Assert.True(read.Percentile(1) > 100_000);
		}

		[Fact]
		public void Merging_adds_the_counts()
		{
			LatencyHistogram fast = new();
			LatencyHistogram slow = new();
			fast.Add(10);
			slow.Add(1000);
			slow.Add(1000);

			fast.Merge(slow);

			Assert.Equal(3, fast.Count);
			Assert.InRange(fast.Percentile(0.5)!.Value, 900, 1100);
		}
	}

	public class ResponseTimeReportTests
	{
		private static readonly DateTime From = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

		private static TimingSample Sample(int minute, string operation, params double[] milliseconds)
		{
			LatencyHistogram histogram = new();
			foreach (double value in milliseconds)
			{
				histogram.Add(value);
			}
			return new TimingSample(From.AddMinutes(minute), operation, histogram);
		}

		[Fact]
		public void Slots_hold_their_minutes_and_empty_slots_have_no_percentiles()
		{
			ResponseTimeReport report = ResponseTimeReport.Build(
				[Sample(0, "hub LoadChapter", 100), Sample(4, "hub LoadChapter", 200), Sample(12, "hub LoadChapter", 300)],
				From,
				From.AddMinutes(15),
				TimeSpan.FromMinutes(5),
				ResponseTimeReport.HubFilter);

			Assert.Equal(3, report.Series.Count);
			Assert.Equal(2, report.Series[0].Count);
			Assert.Equal(0, report.Series[1].Count);
			Assert.Null(report.Series[1].P50);
			Assert.Equal(From.AddMinutes(10), report.Series[2].StartUtc);
			Assert.Equal(1, report.Series[2].Count);
		}

		[Fact]
		public void The_filter_picks_the_series_but_every_operation_is_listed()
		{
			ResponseTimeReport report = ResponseTimeReport.Build(
				[Sample(0, "hub LoadChapter", 100, 100), Sample(0, "GET ReadingPage", 5), Sample(1, "static files", 1, 1, 1)],
				From,
				From.AddMinutes(5),
				TimeSpan.FromMinutes(5),
				ResponseTimeReport.HttpFilter);

			Assert.Equal(4, report.Series.Single().Count);
			Assert.Equal(["static files", "hub LoadChapter", "GET ReadingPage"], report.Operations.Select(operation => operation.Operation));
		}

		[Fact]
		public void Samples_outside_the_range_are_ignored()
		{
			ResponseTimeReport report = ResponseTimeReport.Build(
				[Sample(-1, "hub LoadChapter", 100), Sample(5, "hub LoadChapter", 100)],
				From,
				From.AddMinutes(5),
				TimeSpan.FromMinutes(5),
				"hub LoadChapter");

			Assert.Equal(0, report.Series.Single().Count);
			Assert.Empty(report.Operations);
		}
	}

	/// <summary>Against a real SQLite file, as the account tests are.</summary>
	public sealed class MonitoringStoreTests : IDisposable
	{
		private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"novelreader-monitoring-test-{Guid.NewGuid():N}.db");
		private readonly SqliteMonitoringStore store;
		private readonly ManualTimeProvider clock = new();

		public MonitoringStoreTests()
		{
			string connectionString = $"Data Source={databasePath}";
			SqliteMonitoringStore.EnsureCreated(connectionString);
			store = new SqliteMonitoringStore(connectionString);
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
		public async Task Finished_minutes_are_flushed_and_the_current_one_still_reported()
		{
			ResponseTimeLog log = new(store, clock);
			log.Record("hub LoadChapter", TimeSpan.FromMilliseconds(100));
			clock.Advance(TimeSpan.FromMinutes(1));
			log.Record("hub LoadChapter", TimeSpan.FromMilliseconds(300));

			await log.FlushAsync(everything: false);

			Assert.Single(await store.GetTimingsAsync(DateTime.MinValue.AddYears(2000), DateTime.UtcNow.AddYears(10)));

			ResponseTimeReport report = await log.ReportAsync(TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), ResponseTimeReport.HubFilter);
			Assert.Equal(2, report.Series.Sum(point => point.Count));
			Assert.Equal(1, report.Series[^1].Count);
			Assert.Equal(1, report.Series[^2].Count);
		}

		[Fact]
		public async Task A_minute_written_twice_is_added_up()
		{
			ResponseTimeLog first = new(store, clock);
			first.Record("GET ReadingPage", TimeSpan.FromMilliseconds(10));
			await first.FlushAsync(everything: true);

			// The next process, within the same minute.
			ResponseTimeLog second = new(store, clock);
			second.Record("GET ReadingPage", TimeSpan.FromMilliseconds(10));
			await second.FlushAsync(everything: true);

			TimingSample stored = Assert.Single(await store.GetTimingsAsync(
				clock.GetUtcNow().UtcDateTime.AddHours(-1), clock.GetUtcNow().UtcDateTime.AddHours(1)));
			Assert.Equal(2, stored.Histogram.Count);
		}

		[Fact]
		public async Task Auth_events_are_listed_newest_first_and_counted_by_outcome()
		{
			DateTime now = clock.GetUtcNow().UtcDateTime;
			await store.RecordAuthEventAsync(new AuthEvent(now.AddDays(-3), AuthOutcome.SignInFailed, "anton", "10.0.0.1", null));
			await store.RecordAuthEventAsync(new AuthEvent(now.AddMinutes(-5), AuthOutcome.SignInFailed, "root", "10.0.0.2", null));
			await store.RecordAuthEventAsync(new AuthEvent(now, AuthOutcome.SignedIn, "anton", "10.0.0.1", null));

			IReadOnlyList<AuthEvent> recent = await store.GetAuthEventsAsync(2);
			IReadOnlyDictionary<AuthOutcome, int> lastDay = await store.CountAuthEventsAsync(now.AddDays(-1));

			Assert.Equal([AuthOutcome.SignedIn, AuthOutcome.SignInFailed], recent.Select(authEvent => authEvent.Outcome));
			Assert.Equal("root", recent[1].UserName);
			Assert.Equal(1, lastDay[AuthOutcome.SignInFailed]);
			Assert.Equal(1, lastDay[AuthOutcome.SignedIn]);
		}

		[Fact]
		public async Task Pruning_drops_what_is_past_its_retention()
		{
			DateTime now = clock.GetUtcNow().UtcDateTime;
			await store.SaveErrorsAsync([
				new ErrorEntry(now.AddDays(-40), "Error", "Old", "gone", null),
				new ErrorEntry(now, "Warning", "New", "kept", "System.Exception: kept")]);
			await store.RecordAuthEventAsync(new AuthEvent(now.AddDays(-100), AuthOutcome.RateLimited, null, "10.0.0.3", null));

			await store.PruneAsync(now.AddDays(-30), now.AddDays(-90), now.AddDays(-30));

			ErrorEntry kept = Assert.Single(await store.GetErrorsAsync(10));
			Assert.Equal("System.Exception: kept", kept.Exception);
			Assert.Empty(await store.GetAuthEventsAsync(10));
		}
	}
}
