using NovelReader.Domain.RealTimeReader.Accounts;
using NovelReader.Domain.RealTimeReader.Reading;

namespace NovelReader.Tests
{
	public class InviteTests
	{
		private readonly ManualTimeProvider clock = new();
		private readonly AccountService service;

		public InviteTests()
		{
			InMemoryUserAccountRepository repository = new();
			service = new AccountService(repository, repository, clock);
		}

		private async Task<string> InviteAsync() => (await service.CreateInviteAsync()).Code;

		[Fact]
		public void A_code_is_three_groups_of_four_unambiguous_characters()
		{
			string code = InviteCodes.Generate();

			Assert.Matches("^[A-HJKMNP-Z2-9]{4}-[A-HJKMNP-Z2-9]{4}-[A-HJKMNP-Z2-9]{4}$", code);
			Assert.NotEqual(code, InviteCodes.Generate());
		}

		[Fact]
		public async Task An_invite_expires_a_day_after_it_is_made()
		{
			(_, DateTimeOffset expiresAt) = await service.CreateInviteAsync();

			Assert.Equal(clock.GetUtcNow() + TimeSpan.FromHours(24), expiresAt);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("not-a-code")]
		[InlineData("AAAA-AAAA-AAAA")]   // the right shape, never issued
		public async Task Sign_up_without_a_good_code_is_refused(string? code)
		{
			AccountResult result = await service.SignUpAsync("anton", "a-good-password", code);

			Assert.Equal(AccountFailure.InviteInvalid, result.Failure);
			Assert.False((await service.SignInAsync("anton", "a-good-password")).Succeeded);
		}

		[Fact]
		public async Task A_code_works_once()
		{
			string code = await InviteAsync();

			Assert.True((await service.SignUpAsync("anton", "a-good-password", code)).Succeeded);
			Assert.Equal(AccountFailure.InviteInvalid, (await service.SignUpAsync("other", "a-good-password", code)).Failure);
		}

		[Fact]
		public async Task A_code_is_refused_after_a_day()
		{
			string code = await InviteAsync();
			clock.Advance(InviteCodes.Lifetime);

			Assert.Equal(AccountFailure.InviteInvalid, (await service.SignUpAsync("anton", "a-good-password", code)).Failure);
		}

		[Fact]
		public async Task Case_dashes_and_spaces_in_a_code_are_forgiven()
		{
			string code = await InviteAsync();
			string typed = " " + code.Replace("-", " ").ToLowerInvariant() + " ";

			Assert.True((await service.SignUpAsync("anton", "a-good-password", typed)).Succeeded);
		}

		[Fact]
		public async Task A_taken_name_leaves_the_invite_unspent()
		{
			await service.SignUpAsync("anton", "a-good-password", await InviteAsync());
			string code = await InviteAsync();

			Assert.Equal(AccountFailure.UserNameTaken, (await service.SignUpAsync("Anton", "a-good-password", code)).Failure);
			Assert.True((await service.SignUpAsync("someone-else", "a-good-password", code)).Succeeded);
		}

		[Fact]
		public async Task Without_a_good_code_sign_up_does_not_say_whether_a_name_is_taken()
		{
			await service.SignUpAsync("anton", "a-good-password", await InviteAsync());

			AccountResult result = await service.SignUpAsync("anton", "a-good-password", "AAAA-AAAA-AAAA");

			Assert.Equal(AccountFailure.InviteInvalid, result.Failure);
		}

		[Fact]
		public async Task Passwords_over_128_characters_are_refused()
		{
			string tooLong = new('x', AccountService.MaximumPasswordLength + 1);
			string longest = new('x', AccountService.MaximumPasswordLength);

			Assert.Equal(AccountFailure.PasswordTooLong, (await service.SignUpAsync("anton", tooLong, await InviteAsync())).Failure);
			Assert.True((await service.SignUpAsync("anton", longest, await InviteAsync())).Succeeded);

			Assert.Equal(AccountFailure.UnknownUserOrPassword, (await service.SignInAsync("anton", tooLong)).Failure);
			Assert.Equal(AccountFailure.PasswordTooLong, (await service.ChangePasswordAsync("anton", longest, tooLong)).Failure);
			Assert.Equal(AccountFailure.CurrentPasswordWrong, (await service.ChangePasswordAsync("anton", tooLong, "a-new-password")).Failure);
		}
	}

	public class NovelSlugTests
	{
		[Theory]
		[InlineData("reverend-insanity")]
		[InlineData("shadow-slave")]
		[InlineData("lord-of-the-mysteries-2")]
		public void Source_site_slugs_are_novel_names(string slug) => Assert.True(NovelSlug.IsValid(slug));

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("-leading-dash")]
		[InlineData("Upper-Case")]
		[InlineData("../../admin")]
		[InlineData("novel/chapter-1")]
		[InlineData("novel?page=2")]
		[InlineData("system.users")]
		[InlineData("novel name")]
		public void Anything_else_is_not(string? name) => Assert.False(NovelSlug.IsValid(name));

		[Fact]
		public void Names_are_capped() =>
			Assert.False(NovelSlug.IsValid(new string('a', NovelSlug.MaximumLength + 1)));
	}
}
