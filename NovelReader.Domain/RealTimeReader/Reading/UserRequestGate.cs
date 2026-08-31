using System.Collections.Concurrent;

namespace NovelReader.Domain.RealTimeReader.Reading
{
	/// <summary>
	/// Serialises reading work per user: a second call for the same reader waits for the
	/// first to finish. Different readers never wait on each other.
	///
	/// One reader has one pair of eyes, so their requests only ever need answering one at a
	/// time. Letting them overlap gained nothing and cost plenty — concurrent calls raced to
	/// scrape and store the same chapter (D17).
	///
	/// Prefetch deliberately does *not* take this gate. It must never delay the paragraph a
	/// reader is waiting for (D11); it is kept in check by its own in-flight de-duplication.
	/// </summary>
	public sealed class UserRequestGate
	{
		/// <summary>
		/// One semaphore per user name. Entries are never evicted: the key space is the set
		/// of real readers, which is small and, for now, exactly one (D9).
		/// </summary>
		private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);

		/// <summary>
		/// Waits for this user's turn. Dispose the result to release it — always in a
		/// <c>using</c>, so a failed read cannot wedge the reader out of their own queue.
		/// </summary>
		public async Task<IDisposable> AcquireAsync(string userName, CancellationToken cancellationToken = default)
		{
			SemaphoreSlim gate = gates.GetOrAdd(userName, _ => new SemaphoreSlim(1, 1));
			await gate.WaitAsync(cancellationToken);
			return new Releaser(gate);
		}

		/// <summary>Releases once, however many times it is disposed.</summary>
		private sealed class Releaser(SemaphoreSlim gate) : IDisposable
		{
			private SemaphoreSlim? gate = gate;

			public void Dispose() => Interlocked.Exchange(ref gate, null)?.Release();
		}
	}
}
