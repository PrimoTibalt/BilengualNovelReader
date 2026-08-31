using System.Collections.Concurrent;
using NovelReader.Domain.RealTimeReader.Accounts;

namespace NovelReader.Tests
{
	/// <summary>
	/// Account storage without a file. Keyed case-insensitively, matching the COLLATE NOCASE
	/// primary key the SQLite table uses, so the two agree on what "taken" means.
	/// </summary>
	internal sealed class InMemoryUserAccountRepository : IUserAccountRepository
	{
		private readonly ConcurrentDictionary<string, UserAccount> accounts =
			new(StringComparer.OrdinalIgnoreCase);

		public Task<AccountResult> CreateAsync(string userName, string passwordHash, CancellationToken cancellationToken = default)
		{
			UserAccount account = new()
			{
				UserName = userName,
				PasswordHash = passwordHash,
				CreatedAtUtc = DateTime.UtcNow
			};

			return Task.FromResult(accounts.TryAdd(userName, account)
				? AccountResult.Success(userName)
				: AccountResult.Failed(AccountFailure.UserNameTaken));
		}

		public Task<UserAccount?> FindAsync(string userName, CancellationToken cancellationToken = default)
		{
			accounts.TryGetValue(userName, out UserAccount? account);
			return Task.FromResult(account);
		}
	}
}
