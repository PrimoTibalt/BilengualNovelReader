namespace NovelReader.Domain.RealTimeReader.Review
{
	/// <summary>A reader's review cards, one per saved term.</summary>
	public interface IReviewCardStore
	{
		Task<IReadOnlyList<ReviewCard>> GetAllAsync(string userName, CancellationToken cancellationToken = default);

		/// <summary>Inserts or replaces the card for its term.</summary>
		Task SaveAsync(string userName, ReviewCard card, CancellationToken cancellationToken = default);

		Task RemoveAsync(string userName, string term, CancellationToken cancellationToken = default);
	}
}
