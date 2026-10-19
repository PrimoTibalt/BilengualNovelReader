namespace NovelReader.Domain.Monitoring
{
	/// <summary>One slot of the graph: how many requests started in it and how long they took. Null percentiles mean none did.</summary>
	public sealed record ResponseTimePoint(DateTime StartUtc, long Count, double? P50, double? P95);

	/// <summary>One operation's totals over the whole range.</summary>
	public sealed record OperationTimes(string Operation, long Count, double P50, double P95);

	/// <summary>
	/// The admin panel's response-time graph and table (D45). <see cref="Series"/> follows the
	/// filter; <see cref="Operations"/> always lists every operation, so another can be picked.
	/// </summary>
	public sealed record ResponseTimeReport(
		DateTime FromUtc,
		DateTime ToUtc,
		TimeSpan Slot,
		string Filter,
		IReadOnlyList<ResponseTimePoint> Series,
		IReadOnlyList<OperationTimes> Operations)
	{
		/// <summary>Every hub method — what the reading page spends its time waiting on.</summary>
		public const string HubFilter = "hub";

		/// <summary>Everything that is not a hub method: pages, auth, the admin panel, static files.</summary>
		public const string HttpFilter = "http";

		/// <summary>Hub methods are recorded as <c>hub LoadChapter</c>.</summary>
		public const string HubPrefix = "hub ";

		/// <summary>
		/// <paramref name="filter"/> is <see cref="HubFilter"/>, <see cref="HttpFilter"/> or one
		/// operation's exact name.
		/// </summary>
		public static bool Matches(string filter, string operation) => filter switch
		{
			HubFilter => operation.StartsWith(HubPrefix, StringComparison.Ordinal),
			HttpFilter => !operation.StartsWith(HubPrefix, StringComparison.Ordinal),
			_ => operation == filter
		};

		/// <summary>
		/// Buckets the samples into <paramref name="slot"/>-long slots from
		/// <paramref name="fromUtc"/> to <paramref name="toUtc"/>; samples outside are ignored.
		/// </summary>
		public static ResponseTimeReport Build(
			IEnumerable<TimingSample> samples,
			DateTime fromUtc,
			DateTime toUtc,
			TimeSpan slot,
			string filter)
		{
			int slotCount = Math.Max(1, (int)Math.Ceiling((toUtc - fromUtc) / slot));
			LatencyHistogram[] slots = new LatencyHistogram[slotCount];
			Dictionary<string, LatencyHistogram> operations = new(StringComparer.Ordinal);

			foreach (TimingSample sample in samples)
			{
				if (sample.MinuteUtc < fromUtc || sample.MinuteUtc >= toUtc)
				{
					continue;
				}

				if (!operations.TryGetValue(sample.Operation, out LatencyHistogram? total))
				{
					operations[sample.Operation] = total = new LatencyHistogram();
				}
				total.Merge(sample.Histogram);

				if (Matches(filter, sample.Operation))
				{
					int index = (int)((sample.MinuteUtc - fromUtc) / slot);
					(slots[index] ??= new LatencyHistogram()).Merge(sample.Histogram);
				}
			}

			List<ResponseTimePoint> series = [];
			for (int index = 0; index < slotCount; index++)
			{
				LatencyHistogram? histogram = slots[index];
				series.Add(new ResponseTimePoint(
					fromUtc + slot * index,
					histogram?.Count ?? 0,
					histogram?.Percentile(0.5),
					histogram?.Percentile(0.95)));
			}

			return new ResponseTimeReport(
				fromUtc,
				toUtc,
				slot,
				filter,
				series,
				[.. operations
					.Select(pair => new OperationTimes(
						pair.Key,
						pair.Value.Count,
						pair.Value.Percentile(0.5) ?? 0,
						pair.Value.Percentile(0.95) ?? 0))
					.OrderByDescending(operation => operation.Count)
					.ThenBy(operation => operation.Operation, StringComparer.Ordinal)]);
		}
	}
}
