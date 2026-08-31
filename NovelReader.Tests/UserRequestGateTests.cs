using NovelReader.Domain.RealTimeReader.Reading;

namespace NovelReader.Tests
{
	/// <summary>Per-user serialisation of reading work (D17).</summary>
	public class UserRequestGateTests
	{
		[Fact]
		public async Task Second_call_for_the_same_user_waits_for_the_first()
		{
			UserRequestGate gate = new();

			IDisposable first = await gate.AcquireAsync("anton");
			Task<IDisposable> second = gate.AcquireAsync("anton");

			// Nothing releases it, so the second call must still be waiting.
			Task finished = await Task.WhenAny(second, Task.Delay(200));
			Assert.NotSame(second, finished);
			Assert.False(second.IsCompleted);

			first.Dispose();

			IDisposable acquired = await second.WaitAsync(TimeSpan.FromSeconds(5));
			acquired.Dispose();
		}

		[Fact]
		public async Task Different_users_never_wait_on_each_other()
		{
			UserRequestGate gate = new();

			using IDisposable anton = await gate.AcquireAsync("anton");
			using IDisposable someoneElse = await gate.AcquireAsync("bai-ning-bing")
				.WaitAsync(TimeSpan.FromSeconds(5));

			Assert.NotNull(someoneElse);
		}

		[Fact]
		public async Task A_failed_turn_still_releases_the_gate()
		{
			UserRequestGate gate = new();

			try
			{
				using IDisposable turn = await gate.AcquireAsync("anton");
				throw new InvalidOperationException("the read failed");
			}
			catch (InvalidOperationException)
			{
				// The `using` is what has to survive this.
			}

			using IDisposable next = await gate.AcquireAsync("anton").WaitAsync(TimeSpan.FromSeconds(5));
			Assert.NotNull(next);
		}

		[Fact]
		public async Task Releasing_twice_does_not_let_two_callers_in_at_once()
		{
			UserRequestGate gate = new();

			IDisposable turn = await gate.AcquireAsync("anton");
			turn.Dispose();
			turn.Dispose();

			using IDisposable first = await gate.AcquireAsync("anton").WaitAsync(TimeSpan.FromSeconds(5));

			Task<IDisposable> second = gate.AcquireAsync("anton");
			Task finished = await Task.WhenAny(second, Task.Delay(200));

			Assert.NotSame(second, finished);
			Assert.False(second.IsCompleted);
		}

		[Fact]
		public async Task Calls_for_one_user_do_not_overlap_under_load()
		{
			UserRequestGate gate = new();
			int inFlight = 0;
			int highWaterMark = 0;
			Lock counter = new();

			async Task DoWork()
			{
				using IDisposable turn = await gate.AcquireAsync("anton");

				lock (counter)
				{
					inFlight++;
					highWaterMark = Math.Max(highWaterMark, inFlight);
				}

				await Task.Delay(5);

				lock (counter)
				{
					inFlight--;
				}
			}

			await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => DoWork()));

			Assert.Equal(1, highWaterMark);
		}
	}
}
