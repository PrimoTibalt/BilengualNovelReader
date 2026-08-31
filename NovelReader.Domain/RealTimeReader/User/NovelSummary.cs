namespace NovelReader.Domain.RealTimeReader.User
{
	/// <summary>
	/// What the catalogue says about a novel the reader has open, cached alongside their
	/// bookmark so the library can show more than a slug without asking the source site every
	/// time it is opened (D22).
	///
	/// Everything but <see cref="Slug"/> is optional: the slug is ours and always known, the
	/// rest is whatever the search answered — and it may not have answered at all.
	/// </summary>
	public sealed class NovelSummary
	{
		/// <summary>The name every other call uses. Always present.</summary>
		public required string Slug { get; init; }

		public string? Title { get; init; }
		public int? Rank { get; init; }
		public int? TotalChapters { get; init; }

		/// <summary>
		/// When the catalogue was last *consulted* for this novel — not when it last changed.
		/// Stamped even when the lookup found nothing, so one failure cannot turn into a
		/// lookup on every page load (D22).
		/// </summary>
		public DateTime? CheckedAtUtc { get; init; }

		/// <summary>True once the catalogue has actually told us something.</summary>
		public bool HasDetails => Title is not null || Rank is not null || TotalChapters is not null;
	}
}
