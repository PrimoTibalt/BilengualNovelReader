using NovelReader.Domain.RealTimeReader.Accounts;

namespace NovelReader.Tests
{
	/// <summary>A clock the test moves by hand.</summary>
	internal sealed class ManualTimeProvider : TimeProvider
	{
		private DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

		public override DateTimeOffset GetUtcNow() => now;

		public void Advance(TimeSpan by) => now += by;
	}

	public class SignInThrottleTests
	{
		private readonly ManualTimeProvider clock = new();
		private readonly SignInThrottle throttle;
		private readonly AccountService service;

		public SignInThrottleTests()
		{
			throttle = new SignInThrottle(clock);
			service = new AccountService(new InMemoryUserAccountRepository(), throttle);
		}

		private async Task FailSignInsAsync(string userName, int times)
		{
			for (int i = 0; i < times; i++)
			{
				AccountResult result = await service.SignInAsync(userName, "not-the-password");
				Assert.Equal(AccountFailure.UnknownUserOrPassword, result.Failure);
			}
		}

		[Fact]
		public async Task Five_wrong_passwords_lock_the_name_for_five_minutes()
		{
			await service.SignUpAsync("anton", "a-good-password");
			await FailSignInsAsync("anton", SignInThrottle.AttemptsBeforeLock);

			// Even the right password is refused while the lock holds.
			AccountResult locked = await service.SignInAsync("anton", "a-good-password");
			Assert.Equal(AccountFailure.LockedOut, locked.Failure);
			Assert.Equal(5, locked.RetryAfterMinutes);

			clock.Advance(TimeSpan.FromMinutes(4));
			Assert.Equal(AccountFailure.LockedOut, (await service.SignInAsync("anton", "a-good-password")).Failure);

			clock.Advance(TimeSpan.FromMinutes(1));
			Assert.True((await service.SignInAsync("anton", "a-good-password")).Succeeded);
		}

		[Fact]
		public async Task Four_wrong_passwords_do_not_lock()
		{
			await service.SignUpAsync("anton", "a-good-password");
			await FailSignInsAsync("anton", SignInThrottle.AttemptsBeforeLock - 1);

			Assert.True((await service.SignInAsync("anton", "a-good-password")).Succeeded);
		}

		[Fact]
		public async Task Each_lock_without_a_success_in_between_is_twice_as_long()
		{
			await service.SignUpAsync("anton", "a-good-password");

			foreach (int minutes in new[] { 5, 10, 20, 40 })
			{
				await FailSignInsAsync("anton", SignInThrottle.AttemptsBeforeLock);

				AccountResult locked = await service.SignInAsync("anton", "not-the-password");
				Assert.Equal(AccountFailure.LockedOut, locked.Failure);
				Assert.Equal(minutes, locked.RetryAfterMinutes);

				clock.Advance(TimeSpan.FromMinutes(minutes));
			}
		}

		[Fact]
		public void Locks_stop_growing_at_a_day()
		{
			TimeSpan? lockedFor = null;
			for (int locks = 0; locks < 15; locks++)
			{
				for (int i = 0; i < SignInThrottle.AttemptsBeforeLock; i++)
				{
					Assert.Null(throttle.TryClaimAttempt("anton"));
				}

				lockedFor = throttle.TryClaimAttempt("anton");
				Assert.NotNull(lockedFor);
				// Wait it out and then some, but well inside the day that would forget the name.
				clock.Advance(lockedFor.Value + TimeSpan.FromHours(1));
			}

			Assert.Equal(SignInThrottle.LongestLock, lockedFor);
		}

		[Fact]
		public async Task A_success_starts_the_count_again()
		{
			await service.SignUpAsync("anton", "a-good-password");

			await FailSignInsAsync("anton", SignInThrottle.AttemptsBeforeLock - 1);
			Assert.True((await service.SignInAsync("anton", "a-good-password")).Succeeded);
			await FailSignInsAsync("anton", SignInThrottle.AttemptsBeforeLock - 1);

			Assert.True((await service.SignInAsync("anton", "a-good-password")).Succeeded);
		}

		[Fact]
		public async Task A_success_after_a_lock_resets_its_growth()
		{
			await service.SignUpAsync("anton", "a-good-password");
			await FailSignInsAsync("anton", SignInThrottle.AttemptsBeforeLock);
			clock.Advance(TimeSpan.FromMinutes(5));
			Assert.True((await service.SignInAsync("anton", "a-good-password")).Succeeded);

			await FailSignInsAsync("anton", SignInThrottle.AttemptsBeforeLock);

			Assert.Equal(5, (await service.SignInAsync("anton", "a-good-password")).RetryAfterMinutes);
		}

		[Fact]
		public async Task A_day_without_attempts_forgets_earlier_locks()
		{
			await service.SignUpAsync("anton", "a-good-password");
			await FailSignInsAsync("anton", SignInThrottle.AttemptsBeforeLock);
			clock.Advance(TimeSpan.FromMinutes(5));
			await FailSignInsAsync("anton", SignInThrottle.AttemptsBeforeLock);

			clock.Advance(SignInThrottle.ForgetAfter + TimeSpan.FromMinutes(11));
			await FailSignInsAsync("anton", SignInThrottle.AttemptsBeforeLock);

			Assert.Equal(5, (await service.SignInAsync("anton", "a-good-password")).RetryAfterMinutes);
		}

		[Fact]
		public async Task An_unknown_name_is_locked_exactly_like_a_real_one()
		{
			await service.SignUpAsync("anton", "a-good-password");
			await FailSignInsAsync("anton", SignInThrottle.AttemptsBeforeLock);
			await FailSignInsAsync("nobody", SignInThrottle.AttemptsBeforeLock);

			AccountResult real = await service.SignInAsync("anton", "not-the-password");
			AccountResult unknown = await service.SignInAsync("nobody", "not-the-password");

			Assert.Equal(AccountFailure.LockedOut, real.Failure);
			Assert.Equal(real.Failure, unknown.Failure);
			Assert.Equal(real.Message, unknown.Message);
		}

		[Fact]
		public async Task Casing_does_not_buy_more_tries()
		{
			await service.SignUpAsync("anton", "a-good-password");
			await FailSignInsAsync("Anton", 3);
			await FailSignInsAsync("ANTON", 2);

			Assert.Equal(AccountFailure.LockedOut, (await service.SignInAsync("anton", "a-good-password")).Failure);
		}

		[Fact]
		public async Task Wrong_current_passwords_count_towards_the_sign_in_lock()
		{
			await service.SignUpAsync("anton", "a-good-password");

			for (int i = 0; i < SignInThrottle.AttemptsBeforeLock; i++)
			{
				AccountResult refused = await service.ChangePasswordAsync("anton", "not-the-password", "a-new-password");
				Assert.Equal(AccountFailure.CurrentPasswordWrong, refused.Failure);
			}

			Assert.Equal(AccountFailure.LockedOut,
				(await service.ChangePasswordAsync("anton", "a-good-password", "a-new-password")).Failure);
			Assert.Equal(AccountFailure.LockedOut, (await service.SignInAsync("anton", "a-good-password")).Failure);
		}

		[Fact]
		public async Task A_burst_of_parallel_guesses_gets_five_tries_and_no_more()
		{
			int allowed = 0;
			await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() =>
			{
				if (throttle.TryClaimAttempt("anton") is null)
				{
					Interlocked.Increment(ref allowed);
				}
			})));

			Assert.Equal(SignInThrottle.AttemptsBeforeLock, allowed);
		}
	}
}
