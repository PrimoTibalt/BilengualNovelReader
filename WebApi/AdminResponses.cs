namespace NovelReader
{
	/// <summary>The admin panel's main read (D45): everything but the response times.</summary>
	public sealed record AdminOverviewResponse(
		DateTime GeneratedAt,
		IReadOnlyList<OnlineReaderResponse> Online,
		SignInSummaryResponse SignIns,
		IReadOnlyList<ErrorEntryResponse> Errors,
		IReadOnlyList<ReaderStatsResponse> Readers);

	public sealed record OnlineReaderResponse(string UserName, IReadOnlyList<OnlineConnectionResponse> Connections);

	public sealed record OnlineConnectionResponse(DateTime Since, DateTime LastActive, string? Address, string? UserAgent);

	/// <summary>Counts are keyed by outcome, written <c>sign-in-failed</c>, as in <see cref="AuthEventResponse.Outcome"/>.</summary>
	public sealed record SignInSummaryResponse(
		IReadOnlyDictionary<string, int> LastDay,
		IReadOnlyDictionary<string, int> LastWeek,
		IReadOnlyList<AuthEventResponse> Recent);

	public sealed record AuthEventResponse(DateTime At, string Outcome, string? UserName, string? Address, string? Detail);

	public sealed record ErrorEntryResponse(DateTime At, string Level, string Category, string Message, string? Exception);

	public sealed record ReaderStatsResponse(
		string UserName,
		int SavedWords,
		int SavedThisWeek,
		DateTime? LastSavedAt,
		ReviewStatsResponse Review,
		IReadOnlyList<NovelReadResponse> Novels);

	/// <summary>
	/// A card is made the first time a saved word comes up in a review, so <see cref="Cards"/>
	/// can be fewer than the saved words. <see cref="Due"/> counts the started cards due now,
	/// <see cref="Mature"/> those 21 days apart or more, Anki's word for it.
	/// </summary>
	public sealed record ReviewStatsResponse(int Cards, int New, int Due, int Learning, int Mature);

	public sealed record NovelReadResponse(string Slug, string? Title, int? Chapter, int? Paragraph, int? TotalChapters, DateTime? LastReadAt);

	public sealed record ResponseTimesResponse(
		DateTime From,
		DateTime To,
		int SlotMinutes,
		string Filter,
		IReadOnlyList<ResponseTimePointResponse> Series,
		IReadOnlyList<OperationTimesResponse> Operations);

	/// <summary>Milliseconds; null where nothing ran in the slot.</summary>
	public sealed record ResponseTimePointResponse(DateTime Start, long Count, double? P50, double? P95);

	public sealed record OperationTimesResponse(string Operation, long Count, double P50, double P95);
}
