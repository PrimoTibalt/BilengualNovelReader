namespace NovelReader.Domain.RealTimeReader.User
{
	/// <summary>
	/// Where a reader had got to in one novel. <see cref="ParagraphNumber"/> is the last
	/// paragraph they actually had on screen, not the next one to fetch — resuming puts that
	/// paragraph back at the bottom of the viewport, where they left it.
	/// </summary>
	public sealed class ReadingProgress
	{
		public required string NovelName { get; init; }
		public required int ChapterNumber { get; init; }
		public required int ParagraphNumber { get; init; }
		public required DateTime UpdatedAtUtc { get; init; }
	}
}
