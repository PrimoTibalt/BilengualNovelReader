using NovelReader.Domain.Monitoring;

namespace NovelReader.Monitoring
{
	/// <summary>
	/// Moves the monitoring data that is gathered in memory into the store (D45): finished
	/// minutes of response times and queued errors every few seconds, everything at shutdown,
	/// and once an hour drops what is past its retention.
	/// </summary>
	internal sealed class MonitoringWriter(
		ResponseTimeLog responseTimes,
		ErrorLog errors,
		IMonitoringStore store,
		TimeProvider time,
		ILogger<MonitoringWriter> logger) : BackgroundService
	{
		public static readonly TimeSpan TimingsKeptFor = TimeSpan.FromDays(30);
		public static readonly TimeSpan AuthEventsKeptFor = TimeSpan.FromDays(90);
		public static readonly TimeSpan ErrorsKeptFor = TimeSpan.FromDays(30);

		private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
		private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			using PeriodicTimer timer = new(Interval, time);
			DateTime nextPrune = DateTime.MinValue;
			try
			{
				while (await timer.WaitForNextTickAsync(stoppingToken))
				{
					await WriteAsync(everything: false, stoppingToken);

					DateTime now = time.GetUtcNow().UtcDateTime;
					if (now >= nextPrune)
					{
						nextPrune = now + PruneInterval;
						await TryAsync(() => store.PruneAsync(
							now - TimingsKeptFor, now - AuthEventsKeptFor, now - ErrorsKeptFor, stoppingToken));
					}
				}
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
			}
		}

		public override async Task StopAsync(CancellationToken cancellationToken)
		{
			await base.StopAsync(cancellationToken);
			// The minute under way, and whatever was logged on the way down.
			await WriteAsync(everything: true, cancellationToken);
		}

		private async Task WriteAsync(bool everything, CancellationToken cancellationToken)
		{
			List<ErrorEntry> queued = [];
			while (errors.Pending.TryRead(out ErrorEntry? error))
			{
				queued.Add(error);
			}

			await TryAsync(() => store.SaveErrorsAsync(queued, cancellationToken));
			await TryAsync(() => responseTimes.FlushAsync(everything, cancellationToken));
		}

		/// <summary>
		/// A failed write is logged to the journal and left for the next round. The error log
		/// does not pick up this class's own warnings, so a broken store cannot feed itself.
		/// </summary>
		private async Task TryAsync(Func<Task> write)
		{
			try
			{
				await write();
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception exception)
			{
				logger.LogWarning(exception, "Could not write monitoring data");
			}
		}
	}
}
