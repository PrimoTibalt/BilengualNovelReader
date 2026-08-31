namespace NovelReader.Domain.RealTimeReader.Accounts
{
	/// <summary>
	/// A registered reader. <see cref="UserName"/> is the identity threaded through every
	/// other store (Mongo progress and vocabulary key off it), so it is the account's real
	/// primary key and cannot change.
	/// </summary>
	public sealed class UserAccount
	{
		public required string UserName { get; init; }

		/// <summary>Self-describing hash — algorithm, iterations and salt travel with it.</summary>
		public required string PasswordHash { get; init; }

		public required DateTime CreatedAtUtc { get; init; }
	}
}
