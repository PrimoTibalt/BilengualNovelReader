using System.Text.RegularExpressions;

namespace NovelReader.Domain.RealTimeReader.Accounts
{
	/// <summary>
	/// Sign-up and sign-in rules, kept out of the controller so they can be tested without
	/// a web host.
	/// </summary>
	public sealed partial class AccountService(IUserAccountRepository accounts)
	{
		public const int MinimumPasswordLength = 8;

		[GeneratedRegex(@"^[A-Za-z0-9._-]{3,32}$")]
		private static partial Regex UserNamePattern { get; }

		/// <summary>
		/// Verified against when there is no such account. Without it, an unknown username
		/// returns as fast as the string comparison that rejects an empty hash, while a real
		/// one pays for a full PBKDF2 — a timing difference that answers "does this account
		/// exist?" for anyone who measures it.
		/// </summary>
		private static readonly Lazy<string> DummyHash =
			new(() => PasswordHasher.Hash("no such account"), LazyThreadSafetyMode.ExecutionAndPublication);

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
			UserAccount? account = name.Length == 0 ? null : await accounts.FindAsync(name, cancellationToken);

			// A real PBKDF2 runs either way, so a missing user costs the same as a wrong password.
			string storedHash = account?.PasswordHash ?? DummyHash.Value;
			bool verified = PasswordHasher.Verify(password ?? string.Empty, storedHash);

			if (account is null || !verified)
			{
				return AccountResult.Failed(AccountFailure.UnknownUserOrPassword);
			}

			// The stored spelling wins, so the session carries one canonical name.
			return AccountResult.Success(account.UserName);
		}
	}
}
