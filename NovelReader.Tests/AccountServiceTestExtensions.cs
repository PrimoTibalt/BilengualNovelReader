using NovelReader.Domain.RealTimeReader.Accounts;

namespace NovelReader.Tests
{
	/// <summary>
	/// Sign-up with a fresh invite, for the many tests that are about something else. The
	/// service's own sign-up takes three arguments, so a two-argument call lands here.
	/// </summary>
	internal static class AccountServiceTestExtensions
	{
		public static async Task<AccountResult> SignUpAsync(this AccountService service, string? userName, string? password)
		{
			(string code, _) = await service.CreateInviteAsync();
			return await service.SignUpAsync(userName, password, code);
		}
	}
}
