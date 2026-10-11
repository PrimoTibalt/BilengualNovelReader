using System.Collections.Concurrent;

namespace NovelReader.Domain.RealTimeReader.Accounts
{
	/// <summary>
	/// Locks a username after too many wrong passwords (D41). Five tries, then five minutes;
	/// every further lock without a success in between doubles, up to a day. A success clears
	/// the record, and so does a day without any attempt.
	///
	/// Names are tracked whether or not an account exists. If only real accounts could be
	/// locked, the lock itself would answer "is this name registered?".
	///
	/// Every attempt is counted as a failure when it is claimed, before the password is checked,
	/// and a success then wipes the record. Counting after the check would let a burst of
	/// parallel guesses all pass the "is it locked?" test before any of them was counted.
	///
	/// In memory: a restart forgets every lock. That is a gift to nobody but someone who can
	/// restart the server, and keeps a guessing run from writing to the accounts database.
	/// </summary>
	public sealed class SignInThrottle(TimeProvider time)
	{
		public const int AttemptsBeforeLock = 5;
		public static readonly TimeSpan FirstLock = TimeSpan.FromMinutes(5);
		public static readonly TimeSpan LongestLock = TimeSpan.FromHours(24);

		/// <summary>
		/// A name left alone this long — counted from the end of its lock, if it had one — starts
		/// again from nothing.
		/// </summary>
		public static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(24);

		/// <summary>
		/// Above this many tracked names, forgotten ones are swept out — names are tracked for
		/// anything typed, so without a sweep a stream of made-up names would grow it forever.
		/// </summary>
		private const int SweepAbove = 10_000;

		private sealed class Record
		{
			public int Attempts;
			public int Locks;
			public DateTimeOffset? LockedUntil;
			public DateTimeOffset LastAttempt;

			/// <summary>
			/// When the name was last tried or, if later, when its lock ends. Counting the quiet
			/// from the last attempt alone would forget a day-long lock the moment it ran out.
			/// </summary>
			public DateTimeOffset QuietSince =>
				LockedUntil is { } until && until > LastAttempt ? until : LastAttempt;
		}

		// Ordinal-ignore-case to match the accounts table, where "Anton" and "anton" are one name.
		private readonly ConcurrentDictionary<string, Record> records = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Claims an attempt at this name's password. Null when the attempt may go ahead;
		/// otherwise how long the name stays locked, and the password must not be checked.
		/// </summary>
		public TimeSpan? TryClaimAttempt(string userName)
		{
			DateTimeOffset now = time.GetUtcNow();
			Record record = records.GetOrAdd(userName, _ => new Record { LastAttempt = now });

			TimeSpan? lockedFor;
			lock (record)
			{
				lockedFor = Claim(record, now);
			}

			if (records.Count > SweepAbove)
			{
				Sweep(now);
			}

			return lockedFor;
		}

		/// <summary>The password was right: the name starts again from nothing.</summary>
		public void Succeeded(string userName) => records.TryRemove(userName, out _);

		private static TimeSpan? Claim(Record record, DateTimeOffset now)
		{
			bool forgotten = now - record.QuietSince > ForgetAfter;

			if (record.LockedUntil is { } until)
			{
				if (now < until)
				{
					return until - now;
				}

				// The lock has run out: five more tries, but the next lock will be longer.
				record.LockedUntil = null;
				record.Attempts = 0;
			}

			if (forgotten)
			{
				record.Attempts = 0;
				record.Locks = 0;
			}

			record.LastAttempt = now;
			record.Attempts++;

			if (record.Attempts >= AttemptsBeforeLock)
			{
				// The lock starts with this, the last allowed try: it still goes ahead, and wipes
				// the lock if the password turns out to be right.
				record.Locks++;
				record.LockedUntil = now + LockDuration(record.Locks);
			}

			return null;
		}

		/// <summary>5, 10, 20, 40 … minutes, capped at <see cref="LongestLock"/>.</summary>
		private static TimeSpan LockDuration(int locks)
		{
			// Past 2^9 the cap has long since won; stopping there keeps the shift from overflowing.
			int doublings = Math.Min(locks - 1, 9);
			TimeSpan duration = FirstLock * (1 << doublings);
			return duration < LongestLock ? duration : LongestLock;
		}

		private void Sweep(DateTimeOffset now)
		{
			foreach ((string name, Record record) in records)
			{
				lock (record)
				{
					if (now - record.QuietSince > ForgetAfter)
					{
						records.TryRemove(name, out _);
					}
				}
			}
		}
	}
}
