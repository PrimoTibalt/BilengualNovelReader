namespace NovelReader.Domain.RealTimeReader.Accounts
{
	/// <summary>
	/// Account storage. Usernames are unique and compared case-insensitively, so "Anton" and
	/// "anton" are the same account and cannot both be registered.
	/// </summary>
	public interface IUserAccountRepository
	{
		/// <summary>
		/// Creates an account. Returns <see cref="AccountFailure.UserNameTaken"/> rather than
		/// throwing when the name is gone — the check and the insert race, so uniqueness has
		/// to be settled by the database's own constraint.
		/// </summary>
		Task<AccountResult> CreateAsync(string userName, string passwordHash, CancellationToken cancellationToken = default);

		Task<UserAccount?> FindAsync(string userName, CancellationToken cancellationToken = default);
	}
}
