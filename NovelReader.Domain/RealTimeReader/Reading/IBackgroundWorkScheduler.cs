namespace NovelReader.Domain.RealTimeReader.Reading
{
	/// <summary>
	/// Runs work that must not delay the reader — chapter prefetch, above all. Domain keeps
	/// its zero-package rule, so the implementation (and the logging of failures) lives in
	/// the web layer.
	/// </summary>
	public interface IBackgroundWorkScheduler
	{
		/// <summary>
		/// Queues work and returns immediately. Failures are the scheduler's problem to log;
		/// a prefetch that fails must never surface to the reader.
		/// </summary>
		void Schedule(Func<CancellationToken, Task> work, string description);
	}
}
