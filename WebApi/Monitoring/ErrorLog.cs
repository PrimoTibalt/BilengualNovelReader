using System.Threading.Channels;
using NovelReader.Domain.Monitoring;

namespace NovelReader.Monitoring
{
	/// <summary>
	/// A logging provider that catches every warning and error, exception text and all, for the
	/// admin panel (D45). Logging must never wait on a database, so entries are queued here and
	/// written by <see cref="MonitoringWriter"/>; if they pile up faster than that, the oldest go.
	/// The journal keeps everything regardless.
	/// </summary>
	internal sealed class ErrorLog : ILoggerProvider
	{
		private readonly Channel<ErrorEntry> pending = Channel.CreateBounded<ErrorEntry>(new BoundedChannelOptions(500)
		{
			FullMode = BoundedChannelFullMode.DropOldest,
			SingleReader = true
		});

		public ChannelReader<ErrorEntry> Pending => pending.Reader;

		public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

		public void Dispose() => pending.Writer.TryComplete();

		private sealed class Logger(ErrorLog log, string category) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

			// The writer's own failures are not caught, or a broken database would feed itself.
			public bool IsEnabled(LogLevel logLevel) =>
				logLevel is >= LogLevel.Warning and < LogLevel.None
				&& !category.StartsWith(typeof(MonitoringWriter).FullName!, StringComparison.Ordinal);

			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
			{
				if (!IsEnabled(logLevel))
				{
					return;
				}

				log.pending.Writer.TryWrite(new ErrorEntry(
					DateTime.UtcNow,
					logLevel.ToString(),
					category,
					formatter(state, exception),
					exception?.ToString()));
			}
		}
	}
}
