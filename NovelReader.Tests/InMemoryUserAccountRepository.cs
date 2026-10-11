using System.Collections.Concurrent;
using NovelReader.Domain.RealTimeReader.Accounts;

namespace NovelReader.Tests
{
	/// <summary>
	/// Account storage without a file. Keyed case-insensitively, matching the COLLATE NOCASE
	/// primary key the SQLite table uses, so the two agree on what "taken" means.
	/// </summary>
	internal sealed class InMemoryUserAccountRepository : IUserAccountRepository, IInviteStore
	{
		private readonly ConcurrentDictionary<string, UserAccount> accounts =
			new(StringComparer.OrdinalIgnoreCase);

		private sealed record Invite(DateTimeOffset ExpiresAt, string? UsedBy);

		private readonly Dictionary<string, Invite> invites = [];

		public Task CreateInviteAsync(string codeHash, DateTimeOffset createdAt, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
		{
			lock (invites)
			{
				invites.Add(codeHash, new Invite(expiresAt, null));
			}
			return Task.CompletedTask;
		}

		/// <summary>Both or neither, as the SQLite transaction does.</summary>
		public Task<AccountResult> CreateAsync(
			string userName,
			string passwordHash,
			string inviteCodeHash,
			DateTimeOffset now,
			CancellationToken cancellationToken = default)
		{
			lock (invites)
			{
				if (!invites.TryGetValue(inviteCodeHash, out Invite? invite) || invite.UsedBy is not null || invite.ExpiresAt <= now)
				{
					return Task.FromResult(AccountResult.Failed(AccountFailure.InviteInvalid));
				}

				UserAccount account = new()
				{
					UserName = userName,
					PasswordHash = passwordHash,
					CreatedAtUtc = now.UtcDateTime
				};

				if (!accounts.TryAdd(userName, account))
				{
					return Task.FromResult(AccountResult.Failed(AccountFailure.UserNameTaken));
				}

				invites[inviteCodeHash] = invite with { UsedBy = userName };
				return Task.FromResult(AccountResult.Success(userName));
			}
		}

		public Task<UserAccount?> FindAsync(string userName, CancellationToken cancellationToken = default)
		{
			accounts.TryGetValue(userName, out UserAccount? account);
			return Task.FromResult(account);
		}

		public Task<bool> UpdatePasswordHashAsync(string userName, string passwordHash, CancellationToken cancellationToken = default)
		{
			if (!accounts.TryGetValue(userName, out UserAccount? account))
			{
				return Task.FromResult(false);
			}

			accounts[userName] = new UserAccount
			{
				UserName = account.UserName,
				PasswordHash = passwordHash,
				CreatedAtUtc = account.CreatedAtUtc
			};
			return Task.FromResult(true);
		}
	}
}
