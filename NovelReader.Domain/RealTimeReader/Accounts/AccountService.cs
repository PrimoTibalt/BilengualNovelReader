using System.Text.RegularExpressions;

namespace NovelReader.Domain.RealTimeReader.Accounts
{
	/// <summary>
	/// Sign-up and sign-in rules, kept out of the controller so they can be tested without
	/// a web host.
	/// </summary>
	public sealed partial class AccountService(IUserAccountRepository accounts, IInviteStore invites, TimeProvider time)
	{
		/// <summary>Wrong passwords at the sign-in form (D41).</summary>
		private readonly SignInThrottle signIns = new(time);

		/// <summary>
		/// Wrong current passwords in the drawer, counted apart from sign-ins (D41): someone
		/// guessing at the sign-in form must not stop the signed-in owner from changing the
		/// password, which is exactly what they will want to do.
		/// </summary>
		private readonly SignInThrottle passwordChanges = new(time);

		public const int MinimumPasswordLength = 8;

		/// <summary>
		/// Generous for a passphrase, and small enough that nobody can make the server hash a
		/// megabyte per request (D42).
		/// </summary>
		public const int MaximumPasswordLength = 128;

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

		/// <summary>
		/// A new invite code (D42), for whoever runs <c>NovelReader invite</c> on the server.
		/// The code itself is returned once and never stored.
		/// </summary>
		public async Task<(string Code, DateTimeOffset ExpiresAt)> CreateInviteAsync(CancellationToken cancellationToken = default)
		{
			string code = InviteCodes.Generate();
			DateTimeOffset now = time.GetUtcNow();
			DateTimeOffset expiresAt = now + InviteCodes.Lifetime;

			await invites.CreateInviteAsync(InviteCodes.Hash(code)!, now, expiresAt, cancellationToken);
			return (code, expiresAt);
		}

		public async Task<AccountResult> SignUpAsync(string? userName, string? password, string? inviteCode, CancellationToken cancellationToken = default)
		{
			string name = (userName ?? string.Empty).Trim();

			if (!UserNamePattern.IsMatch(name))
			{
				return AccountResult.Failed(AccountFailure.UserNameInvalid);
			}

			if (PasswordLengthFailure(password) is { } lengthFailure)
			{
				return AccountResult.Failed(lengthFailure);
			}

			// Something that cannot be a code is turned away before any hashing or lookup.
			if (InviteCodes.Hash(inviteCode) is not { } inviteHash)
			{
				return AccountResult.Failed(AccountFailure.InviteInvalid);
			}

			string hash = PasswordHasher.Hash(password!);
			return await accounts.CreateAsync(name, hash, inviteHash, time.GetUtcNow(), cancellationToken);
		}

		private static AccountFailure? PasswordLengthFailure(string? password) => (password ?? string.Empty).Length switch
		{
			< MinimumPasswordLength => AccountFailure.PasswordTooShort,
			> MaximumPasswordLength => AccountFailure.PasswordTooLong,
			_ => null
		};

		public async Task<AccountResult> SignInAsync(string? userName, string? password, CancellationToken cancellationToken = default)
		{
			string name = (userName ?? string.Empty).Trim();

			// Before the lookup, and the same for names that exist and names that do not, so a
			// locked answer — and how fast it comes — says nothing about which this is (D41).
			if (name.Length > 0 && signIns.TryClaimAttempt(name) is { } lockedFor)
			{
				return AccountResult.LockedOut(lockedFor);
			}

			// No password that long can be stored, so it cannot be right — and it is refused
			// unhashed, the same for every name, so it gives nothing away.
			if ((password ?? string.Empty).Length > MaximumPasswordLength)
			{
				return AccountResult.Failed(AccountFailure.UnknownUserOrPassword);
			}

			UserAccount? account = name.Length == 0 ? null : await accounts.FindAsync(name, cancellationToken);

			// A real PBKDF2 runs either way, so a missing user costs the same as a wrong password.
			string storedHash = account?.PasswordHash ?? DummyHash;
			bool verified = PasswordHasher.Verify(password ?? string.Empty, storedHash);

			if (account is null || !verified)
			{
				return AccountResult.Failed(AccountFailure.UnknownUserOrPassword);
			}

			signIns.Succeeded(name);

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
			if (PasswordLengthFailure(newPassword) is { } lengthFailure)
			{
				return AccountResult.Failed(lengthFailure);
			}

			// Five tries here too, so an open session is no way to guess without limit (D41).
			if (passwordChanges.TryClaimAttempt(userName) is { } lockedFor)
			{
				return AccountResult.LockedOut(lockedFor);
			}

			UserAccount? account = await accounts.FindAsync(userName, cancellationToken);
			if (account is null)
			{
				return AccountResult.Failed(AccountFailure.UnknownUserOrPassword);
			}

			string current = currentPassword ?? string.Empty;
			if (current.Length > MaximumPasswordLength || !PasswordHasher.Verify(current, account.PasswordHash))
			{
				return AccountResult.Failed(AccountFailure.CurrentPasswordWrong);
			}

			passwordChanges.Succeeded(userName);

			string hash = PasswordHasher.Hash(newPassword!);
			bool updated = await accounts.UpdatePasswordHashAsync(userName, hash, cancellationToken);

			return updated
				? AccountResult.Success(userName)
				: AccountResult.Failed(AccountFailure.UnknownUserOrPassword);
		}
	}
}
