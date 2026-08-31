using NovelReader.Domain.RealTimeReader.Accounts;

namespace NovelReader.Tests
{
	public class AccountServiceTests
	{
		private static AccountService Build() => new(new InMemoryUserAccountRepository());

		[Theory]
		[InlineData("ab")]                                  // too short
		[InlineData("this-name-is-far-too-long-to-be-allowed-here")]
		[InlineData("has spaces")]
		[InlineData("has/slash")]
		[InlineData("")]
		public async Task Invalid_usernames_are_rejected(string userName)
		{
			AccountResult result = await Build().SignUpAsync(userName, "a-good-password");

			Assert.False(result.Succeeded);
			Assert.Equal(AccountFailure.UserNameInvalid, result.Failure);
		}

		[Fact]
		public async Task Short_passwords_are_rejected()
		{
			AccountResult result = await Build().SignUpAsync("anton", "short");

			Assert.False(result.Succeeded);
			Assert.Equal(AccountFailure.PasswordTooShort, result.Failure);
		}

		[Fact]
		public async Task A_username_can_only_be_taken_once()
		{
			AccountService service = Build();

			Assert.True((await service.SignUpAsync("anton", "a-good-password")).Succeeded);

			AccountResult second = await service.SignUpAsync("anton", "another-password");
			Assert.False(second.Succeeded);
			Assert.Equal(AccountFailure.UserNameTaken, second.Failure);
		}

		[Fact]
		public async Task Usernames_collide_regardless_of_case()
		{
			AccountService service = Build();
			await service.SignUpAsync("Anton", "a-good-password");

			AccountResult second = await service.SignUpAsync("anton", "a-good-password");

			Assert.Equal(AccountFailure.UserNameTaken, second.Failure);
		}

		[Fact]
		public async Task Signing_in_returns_the_name_as_registered()
		{
			AccountService service = Build();
			await service.SignUpAsync("Anton", "a-good-password");

			// Every other store keys off this name, so the canonical spelling has to win.
			AccountResult result = await service.SignInAsync("anton", "a-good-password");

			Assert.True(result.Succeeded);
			Assert.Equal("Anton", result.UserName);
		}

		[Fact]
		public async Task A_wrong_password_and_an_unknown_user_are_indistinguishable()
		{
			AccountService service = Build();
			await service.SignUpAsync("anton", "a-good-password");

			AccountResult wrongPassword = await service.SignInAsync("anton", "not-the-password");
			AccountResult noSuchUser = await service.SignInAsync("nobody", "not-the-password");

			Assert.Equal(AccountFailure.UnknownUserOrPassword, wrongPassword.Failure);
			Assert.Equal(AccountFailure.UnknownUserOrPassword, noSuchUser.Failure);
			Assert.Equal(wrongPassword.Message, noSuchUser.Message);
		}

		[Fact]
		public async Task A_username_is_trimmed_before_it_is_stored()
		{
			AccountService service = Build();

			Assert.True((await service.SignUpAsync("  anton  ", "a-good-password")).Succeeded);
			Assert.True((await service.SignInAsync("anton", "a-good-password")).Succeeded);
		}
	}
}
