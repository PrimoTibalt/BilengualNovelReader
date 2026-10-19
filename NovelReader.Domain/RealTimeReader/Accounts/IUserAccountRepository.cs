namespace NovelReader.Domain.RealTimeReader.Accounts
{
	/// <summary>
	/// Account storage. Usernames are unique and compared case-insensitively, so "Anton" and
	/// "anton" are the same account and cannot both be registered.
	/// </summary>
	public interface IUserAccountRepository
	{
		/// <summary>
		/// Spends the invite and creates the account, both or neither (D42). Answers
		/// <see cref="AccountFailure.InviteInvalid"/> when the code is unknown, spent or past
		/// <paramref name="now"/>, and <see cref="AccountFailure.UserNameTaken"/> — leaving the
		/// invite unspent — when the name is gone. The invite is checked first, so a name can
		/// only be found to be taken by someone holding a good code.
		///
		/// Uniqueness is the database's constraint to settle, not a check beforehand: the check
		/// and the insert would race.
		/// </summary>
		Task<AccountResult> CreateAsync(
			string userName,
			string passwordHash,
			string inviteCodeHash,
			DateTimeOffset now,
			CancellationToken cancellationToken = default);

		Task<UserAccount?> FindAsync(string userName, CancellationToken cancellationToken = default);

		/// <summary>Every account's name, in no particular order — for the admin panel (D45).</summary>
		Task<IReadOnlyList<string>> ListUserNamesAsync(CancellationToken cancellationToken = default);

		/// <summary>Replaces the account's password hash. False when there is no such account.</summary>
		Task<bool> UpdatePasswordHashAsync(string userName, string passwordHash, CancellationToken cancellationToken = default);
	}
}
