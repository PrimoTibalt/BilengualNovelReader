using NovelReader.Domain.Monitoring;

namespace NovelReader.Monitoring
{
	/// <summary>
	/// Writes sign-in, sign-up and password-change attempts to the monitoring store (D45). A
	/// failure to write is logged and swallowed: nobody's sign-in should fail because the
	/// record of it could not be kept.
	/// </summary>
	public sealed class AuthEventRecorder(IMonitoringStore store, TimeProvider time, ILogger<AuthEventRecorder> logger)
	{
		/// <summary>A typed name is kept to this length; anything longer is not a name anyway.</summary>
		private const int MaxNameLength = 64;

		public async Task RecordAsync(AuthOutcome outcome, string? userName, HttpContext? http, string? detail = null)
		{
			string? name = string.IsNullOrWhiteSpace(userName)
				? null
				: userName.Length > MaxNameLength ? userName[..MaxNameLength] : userName;

			try
			{
				await store.RecordAuthEventAsync(new AuthEvent(
					time.GetUtcNow().UtcDateTime,
					outcome,
					name,
					// The client's own address: UseForwardedHeaders has taken it from nginx.
					http?.Connection.RemoteIpAddress?.ToString(),
					detail));
			}
			catch (Exception exception)
			{
				logger.LogWarning(exception, "Could not record a {Outcome} attempt", outcome);
			}
		}
	}
}
