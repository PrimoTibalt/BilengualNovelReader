namespace NovelReader.Domain.Monitoring
{
	/// <summary>
	/// Collects every request's and hub call's duration (D45). The current minute is counted
	/// in memory; minutes that are over are written to the store by <see cref="FlushAsync"/>,
	/// which the host calls every few seconds and once more at shutdown.
	///
	/// Flushing and reporting take turns, so a minute moving from memory to the store while a
	/// report is being built is counted once — not twice, and not missed.
	/// </summary>
	public sealed class ResponseTimeLog(IMonitoringStore store, TimeProvider time)
	{
		private readonly Lock gate = new();
		private readonly Dictionary<(DateTime Minute, string Operation), LatencyHistogram> open = [];
		private readonly SemaphoreSlim storeTurn = new(1, 1);

		public void Record(string operation, TimeSpan elapsed)
		{
			DateTime minute = MinuteOf(time.GetUtcNow().UtcDateTime);
			lock (gate)
			{
				if (!open.TryGetValue((minute, operation), out LatencyHistogram? histogram))
				{
					open[(minute, operation)] = histogram = new LatencyHistogram();
				}
				histogram.Add(elapsed.TotalMilliseconds);
			}
		}

		/// <summary>
		/// Writes the minutes that are over to the store — or, with <paramref name="everything"/>
		/// at shutdown, the current one too. A failed write puts them back to be tried again.
		/// </summary>
		public async Task FlushAsync(bool everything, CancellationToken cancellationToken = default)
		{
			await storeTurn.WaitAsync(cancellationToken);
			try
			{
				DateTime current = MinuteOf(time.GetUtcNow().UtcDateTime);
				List<TimingSample> taken = [];
				lock (gate)
				{
					foreach (((DateTime minute, string operation), LatencyHistogram histogram) in open)
					{
						if (everything || minute < current)
						{
							taken.Add(new TimingSample(minute, operation, histogram));
						}
					}
					foreach (TimingSample sample in taken)
					{
						open.Remove((sample.MinuteUtc, sample.Operation));
					}
				}

				if (taken.Count == 0)
				{
					return;
				}

				try
				{
					await store.SaveTimingsAsync(taken, cancellationToken);
				}
				catch
				{
					lock (gate)
					{
						foreach (TimingSample sample in taken)
						{
							if (open.TryGetValue((sample.MinuteUtc, sample.Operation), out LatencyHistogram? since))
							{
								since.Merge(sample.Histogram);
							}
							else
							{
								open[(sample.MinuteUtc, sample.Operation)] = sample.Histogram;
							}
						}
					}
					throw;
				}
			}
			finally
			{
				storeTurn.Release();
			}
		}

		/// <summary>
		/// The last <paramref name="span"/>, in slots of <paramref name="slot"/> lined up on
		/// multiples of it (UTC), the last slot being the one under way.
		/// </summary>
		public async Task<ResponseTimeReport> ReportAsync(
			TimeSpan span,
			TimeSpan slot,
			string filter,
			CancellationToken cancellationToken = default)
		{
			DateTime now = time.GetUtcNow().UtcDateTime;
			int slotCount = Math.Max(1, (int)Math.Round(span / slot));
			DateTime currentSlot = new(now.Ticks - now.Ticks % slot.Ticks, DateTimeKind.Utc);
			DateTime from = currentSlot - slot * (slotCount - 1);
			DateTime to = currentSlot + slot;

			await storeTurn.WaitAsync(cancellationToken);
			try
			{
				List<TimingSample> samples = [.. await store.GetTimingsAsync(from, to, cancellationToken)];
				lock (gate)
				{
					samples.AddRange(open.Select(pair =>
						new TimingSample(pair.Key.Minute, pair.Key.Operation, pair.Value.Clone())));
				}

				return ResponseTimeReport.Build(samples, from, to, slot, filter);
			}
			finally
			{
				storeTurn.Release();
			}
		}

		private static DateTime MinuteOf(DateTime utc) =>
			new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
	}
}
