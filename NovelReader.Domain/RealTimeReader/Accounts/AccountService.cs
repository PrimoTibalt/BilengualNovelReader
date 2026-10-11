using System.Text.RegularExpressions;

namespace NovelReader.Domain.RealTimeReader.Accounts
{
	/// <summary>
	/// Sign-up and sign-in rules, kept out of the controller so they can be tested without
	/// a web host.
	/// </summary>
	public sealed partial class AccountService(IUserAccountRepository accounts, SignInThrottle throttle)
	{
		public const int MinimumPasswordLength = 8;

		[GeneratedRegex(@"^[A-Za-z0-9._-]{3,32}$")]
		private static partial Regex UserNamePattern { get; }

		/// <summary>
		/// Verified against when there is no such account. Without it, an unknown username
		/// returns as fast as the string comparison that rejects an empty hash, while a real
		/// one pays for a full PBKDF2 — a timing difference that answers "does this account
		/// exist?" for anyone who measures it.
		///
		/// Computed when the type is first touched, not on the first unknown name: lazily, that
		/// one sign-in would pay for two PBKDF2s. The host resolves the service at start-up so the
		/// cost lands before any request does (D41).
		/// </summary>
		private static readonly string DummyHash = PasswordHasher.Hash("no such account");

		public async Task<AccountResult> SignUpAsync(string? userName, string? password, CancellationToken cancellationToken = default)
		{
			string name = (userName ?? string.Empty).Trim();

			if (!UserNamePattern.IsMatch(name))
			{
				return AccountResult.Failed(AccountFailure.UserNameInvalid);
			}

			if ((password ?? string.Empty).Length < MinimumPasswordLength)
			{
				return AccountResult.Failed(AccountFailure.PasswordTooShort);
			}

			string hash = PasswordHasher.Hash(password!);
			return await accounts.CreateAsync(name, hash, cancellationToken);
		}

		public async Task<AccountResult> SignInAsync(string? userName, string? password, CancellationToken cancellationToken = default)
		{
			string name = (userName ?? string.Empty).Trim();

			// Before the lookup, and the same for names that exist and names that do not, so a
			// locked answer — and how fast it comes — says nothing about which this is (D41).
			if (name.Length > 0 && throttle.TryClaimAttempt(name) is { } lockedFor)
			{
				return AccountResult.LockedOut(lockedFor);
			}

			UserAccount? account = name.Length == 0 ? null : await accounts.FindAsync(name, cancellationToken);

			// A real PBKDF2 runs either way, so a missing user costs the same as a wrong password.
			string storedHash = account?.PasswordHash ?? DummyHash;
			bool verified = PasswordHasher.Verify(password ?? string.Empty, storedHash);

			if (account is null || !verified)
			{
				return AccountResult.Failed(AccountFailure.UnknownUserOrPassword);
			}

			throttle.Succeeded(name);

			// The stored spelling wins, so the session carries one canonical name.
			return AccountResult.Success(account.UserName);
		}

		/// <summary>
		/// Sets a signed-in reader's password. The current one is required even though the
		/// cookie already says who this is, so a session left open on someone else's screen
		/// cannot be turned into the account. The new one is typed once — the page lets the
		/// reader look at it instead of typing it twice. Other signed-in devices stay signed in.
		/// </summary>
		public async Task<AccountResult> ChangePasswordAsync(
			string userName,
			string? currentPassword,
			string? newPassword,
			CancellationToken cancellationToken = default)
		{
			if ((newPassword ?? string.Empty).Length < MinimumPasswordLength)
			{
				return AccountResult.Failed(AccountFailure.PasswordTooShort);
			}

			// The same five tries as sign-in, counted together: guessing from an open session must
			// not be a way round the sign-in lock (D41).
			if (throttle.TryClaimAttempt(userName) is { } lockedFor)
			{
				return AccountResult.LockedOut(lockedFor);
			}

			UserAccount? account = await accounts.FindAsync(userName, cancellationToken);
			if (account is null)
			{
				return AccountResult.Failed(AccountFailure.UnknownUserOrPassword);
			}

			if (!PasswordHasher.Verify(currentPassword ?? string.Empty, account.PasswordHash))
			{
				return AccountResult.Failed(AccountFailure.CurrentPasswordWrong);
			}

			throttle.Succeeded(userName);

			string hash = PasswordHasher.Hash(newPassword!);
			bool updated = await accounts.UpdatePasswordHashAsync(userName, hash, cancellationToken);

			return updated
				? AccountResult.Success(userName)
				: AccountResult.Failed(AccountFailure.UnknownUserOrPassword);
		}
	}
}
