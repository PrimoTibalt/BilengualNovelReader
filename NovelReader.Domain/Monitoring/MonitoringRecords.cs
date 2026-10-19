namespace NovelReader.Domain.Monitoring
{
	/// <summary>What became of one attempt to get into an account (D45).</summary>
	public enum AuthOutcome
	{
		SignedIn,
		/// <summary>Unknown name or wrong password — the two are one outcome, as they are to the reader (D41).</summary>
		SignInFailed,
		SignInLocked,
		/// <summary>Refused by the per-address limit (D42) before any password was checked.</summary>
		RateLimited,
		SignedUp,
		SignUpRefused,
		PasswordChangeRefused,
		PasswordChangeLocked
	}

	/// <summary>
	/// One sign-in, sign-up or password-change attempt. <see cref="UserName"/> is the name as
	/// typed, which need not be an account; <see cref="Detail"/> says why a refusal was one.
	/// </summary>
	public sealed record AuthEvent(DateTime AtUtc, AuthOutcome Outcome, string? UserName, string? Address, string? Detail);

	/// <summary>A warning or error the app logged, with the exception's full text when there was one.</summary>
	public sealed record ErrorEntry(DateTime AtUtc, string Level, string Category, string Message, string? Exception);

	/// <summary>One operation's response times within one minute.</summary>
	public sealed record TimingSample(DateTime MinuteUtc, string Operation, LatencyHistogram Histogram);
}
