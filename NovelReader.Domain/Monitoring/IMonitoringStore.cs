namespace NovelReader.Domain.Monitoring
{
	/// <summary>
	/// What the admin panel shows that has to outlive a restart (D45): sign-in attempts, logged
	/// errors and per-minute response times. Kept apart from the accounts, so losing it loses
	/// nothing a reader would miss.
	/// </summary>
	public interface IMonitoringStore
	{
		Task RecordAuthEventAsync(AuthEvent authEvent, CancellationToken cancellationToken = default);

		/// <summary>Newest first.</summary>
		Task<IReadOnlyList<AuthEvent>> GetAuthEventsAsync(int limit, CancellationToken cancellationToken = default);

		/// <summary>How many attempts of each outcome there were since <paramref name="sinceUtc"/>.</summary>
		Task<IReadOnlyDictionary<AuthOutcome, int>> CountAuthEventsAsync(DateTime sinceUtc, CancellationToken cancellationToken = default);

		Task SaveErrorsAsync(IReadOnlyList<ErrorEntry> errors, CancellationToken cancellationToken = default);

		/// <summary>Newest first.</summary>
		Task<IReadOnlyList<ErrorEntry>> GetErrorsAsync(int limit, CancellationToken cancellationToken = default);

		/// <summary>
		/// Adds the samples to whatever is stored for the same minute and operation — a minute
		/// written at shutdown is carried on by the next process.
		/// </summary>
		Task SaveTimingsAsync(IReadOnlyList<TimingSample> samples, CancellationToken cancellationToken = default);

		/// <summary>Every sample whose minute starts in <c>[fromUtc, toUtc)</c>.</summary>
		Task<IReadOnlyList<TimingSample>> GetTimingsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

		/// <summary>Drops everything older than its retention.</summary>
		Task PruneAsync(DateTime timingsBeforeUtc, DateTime eventsBeforeUtc, DateTime errorsBeforeUtc, CancellationToken cancellationToken = default);
	}
}
