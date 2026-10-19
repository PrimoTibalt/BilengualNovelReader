using System.Globalization;
using System.Text;

namespace NovelReader.Domain.Monitoring
{
	/// <summary>
	/// Response times counted into buckets on a log scale, so a minute of traffic is a few dozen
	/// counters however many requests it held, and minutes (or operations) merge by adding them
	/// up (D45). Bucket 0 holds everything under <see cref="FloorMilliseconds"/>; bucket
	/// <c>i</c> holds <c>[Floor × Growth^(i−1), Floor × Growth^i)</c>, and the last one
	/// everything above. A percentile is read back as its bucket's geometric middle, within
	/// about ±10% of the true time — plenty to see whether a call takes 80 ms or 800.
	/// </summary>
	public sealed class LatencyHistogram
	{
		public const double FloorMilliseconds = 0.5;
		public const double Growth = 1.2;

		/// <summary>The last bucket starts at about 175 s; anything slower is counted there.</summary>
		public const int BucketCount = 72;

		private readonly long[] counts = new long[BucketCount];

		public long Count { get; private set; }

		public void Add(double milliseconds)
		{
			counts[BucketOf(milliseconds)]++;
			Count++;
		}

		public void Merge(LatencyHistogram other)
		{
			for (int bucket = 0; bucket < BucketCount; bucket++)
			{
				counts[bucket] += other.counts[bucket];
			}
			Count += other.Count;
		}

		public LatencyHistogram Clone()
		{
			LatencyHistogram copy = new();
			copy.Merge(this);
			return copy;
		}

		/// <summary>
		/// The time below which <paramref name="quantile"/> of the requests finished (0.5 for the
		/// median, 0.95 for p95), in milliseconds. Null when nothing was counted.
		/// </summary>
		public double? Percentile(double quantile)
		{
			if (Count == 0)
			{
				return null;
			}

			long rank = Math.Max(1, (long)Math.Ceiling(quantile * Count));
			long seen = 0;
			for (int bucket = 0; bucket < BucketCount; bucket++)
			{
				seen += counts[bucket];
				if (seen >= rank)
				{
					return MiddleOf(bucket);
				}
			}

			return MiddleOf(BucketCount - 1);
		}

		/// <summary>Only the buckets that counted something, as <c>bucket:count</c> pairs.</summary>
		public string ToText()
		{
			StringBuilder text = new();
			for (int bucket = 0; bucket < BucketCount; bucket++)
			{
				if (counts[bucket] == 0)
				{
					continue;
				}

				if (text.Length > 0)
				{
					text.Append(',');
				}
				text.Append(bucket.ToString(CultureInfo.InvariantCulture))
					.Append(':')
					.Append(counts[bucket].ToString(CultureInfo.InvariantCulture));
			}
			return text.ToString();
		}

		/// <summary>Reads <see cref="ToText"/> back; pairs it cannot read are skipped.</summary>
		public static LatencyHistogram Parse(string text)
		{
			LatencyHistogram histogram = new();
			foreach (string pair in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
			{
				string[] parts = pair.Split(':');
				if (parts.Length == 2
					&& int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int bucket)
					&& long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long count)
					&& bucket is >= 0 and < BucketCount)
				{
					histogram.counts[bucket] += count;
					histogram.Count += count;
				}
			}
			return histogram;
		}

		internal static int BucketOf(double milliseconds)
		{
			if (!(milliseconds >= FloorMilliseconds))
			{
				return 0;
			}

			int bucket = 1 + (int)Math.Floor(Math.Log(milliseconds / FloorMilliseconds) / Math.Log(Growth));
			return Math.Min(bucket, BucketCount - 1);
		}

		internal static double MiddleOf(int bucket) => bucket == 0
			? FloorMilliseconds / 2
			: FloorMilliseconds * Math.Pow(Growth, bucket - 1) * Math.Sqrt(Growth);
	}
}
