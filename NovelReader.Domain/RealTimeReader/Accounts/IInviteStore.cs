namespace NovelReader.Domain.RealTimeReader.Accounts
{
	/// <summary>
	/// Where invite codes are kept (D42). Codes are spent by
	/// <see cref="IUserAccountRepository.CreateAsync"/>, in the same transaction that creates the
	/// account, so a code cannot be spent without an account appearing or the other way round.
	/// </summary>
	public interface IInviteStore
	{
		Task CreateInviteAsync(string codeHash, DateTimeOffset createdAt, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
	}
}
