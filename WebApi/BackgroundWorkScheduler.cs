using NovelReader.Domain.RealTimeReader.Reading;

namespace NovelReader
{
	/// <summary>
	/// Fire-and-forget work that must not delay a reader waiting on a paragraph. Failures
	/// are logged and swallowed: a prefetch that could not run is a missed optimisation, not
	/// an error the reader should ever see.
	/// </summary>
	public class BackgroundWorkScheduler(
		ILogger<BackgroundWorkScheduler> logger,
		IHostApplicationLifetime applicationLifetime) : IBackgroundWorkScheduler
	{
		public void Schedule(Func<CancellationToken, Task> work, string description)
		{
			_ = Task.Run(async () =>
			{
				try
				{
					await work(applicationLifetime.ApplicationStopping);
				}
				catch (OperationCanceledException)
				{
					// Shutdown, or the work was no longer wanted.
				}
				catch (Exception exception)
				{
					logger.LogWarning(exception, "Background work failed: {Description}", description);
				}
			});
		}
	}
}
