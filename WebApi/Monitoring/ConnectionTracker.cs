using System.Collections.Concurrent;

namespace NovelReader.Monitoring
{
	/// <summary>One open hub connection — a reading page open on some device.</summary>
	public sealed class OnlineConnection(string userName, DateTime sinceUtc, string? address, string? userAgent)
	{
		private long lastActiveTicks = sinceUtc.Ticks;

		public string UserName { get; } = userName;
		public DateTime SinceUtc { get; } = sinceUtc;
		public string? Address { get; } = address;
		public string? UserAgent { get; } = userAgent;

		/// <summary>When the page last called the hub: a page left open in a background tab goes quiet.</summary>
		public DateTime LastActiveUtc => new(Interlocked.Read(ref lastActiveTicks), DateTimeKind.Utc);

		public void Touch(DateTime utc) => Interlocked.Exchange(ref lastActiveTicks, utc.Ticks);
	}

	/// <summary>
	/// Who is online: every open hub connection, in memory (D45). Kept by
	/// <see cref="HubMonitoringFilter"/>, which sees each connection open and close.
	/// </summary>
	public sealed class ConnectionTracker
	{
		private readonly ConcurrentDictionary<string, OnlineConnection> connections = new();

		public void Opened(string connectionId, OnlineConnection connection) => connections[connectionId] = connection;

		public void Closed(string connectionId) => connections.TryRemove(connectionId, out _);

		public void Touch(string connectionId, DateTime utc)
		{
			if (connections.TryGetValue(connectionId, out OnlineConnection? connection))
			{
				connection.Touch(utc);
			}
		}

		public IReadOnlyList<OnlineConnection> Snapshot() => [.. connections.Values];
	}
}
