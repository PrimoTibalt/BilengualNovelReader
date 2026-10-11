using NovelReader.Domain.RealTimeReader.Accounts;

namespace NovelReader.Tests
{
	public class AccountServiceTests
	{
		private static AccountService Build() => new(new InMemoryUserAccountRepository(), new SignInThrottle(TimeProvider.System));

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

		[Fact]
		public async Task A_changed_password_replaces_the_old_one()
		{
			AccountService service = Build();
			await service.SignUpAsync("anton", "a-good-password");

			AccountResult changed = await service.ChangePasswordAsync("anton", "a-good-password", "a-new-password");

			Assert.True(changed.Succeeded);
			Assert.True((await service.SignInAsync("anton", "a-new-password")).Succeeded);
			Assert.False((await service.SignInAsync("anton", "a-good-password")).Succeeded);
		}

		[Fact]
		public async Task A_short_new_password_is_refused_and_the_old_one_kept()
		{
			AccountService service = Build();
			await service.SignUpAsync("anton", "a-good-password");

			AccountResult changed = await service.ChangePasswordAsync("anton", "a-good-password", "short");

			Assert.Equal(AccountFailure.PasswordTooShort, changed.Failure);
			Assert.True((await service.SignInAsync("anton", "a-good-password")).Succeeded);
		}

		[Theory]
		[InlineData("not-the-password")]
		[InlineData("")]
		[InlineData(null)]
		public async Task A_wrong_current_password_is_refused_and_the_old_one_kept(string? currentPassword)
		{
			AccountService service = Build();
			await service.SignUpAsync("anton", "a-good-password");

			AccountResult changed = await service.ChangePasswordAsync("anton", currentPassword, "a-new-password");

			Assert.Equal(AccountFailure.CurrentPasswordWrong, changed.Failure);
			Assert.True((await service.SignInAsync("anton", "a-good-password")).Succeeded);
			Assert.False((await service.SignInAsync("anton", "a-new-password")).Succeeded);
		}

		[Fact]
		public async Task Changing_the_password_of_a_missing_account_fails()
		{
			AccountResult changed = await Build().ChangePasswordAsync("nobody", "a-good-password", "a-new-password");

			Assert.False(changed.Succeeded);
		}
	}
}
