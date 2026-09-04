namespace NovelReader.Domain.RealTimeReader.Translation
{
	/// <summary>
	/// Where a reader's translation settings live. Both columns are nullable: a reader who has
	/// never pressed <c>t</c> has neither, which is exactly how the page knows to ask.
	/// </summary>
	public interface ITranslationSettingsStore
	{
		/// <summary>Null when this reader has not set them up yet.</summary>
		Task<TranslationSettings?> GetAsync(string userName, CancellationToken cancellationToken = default);

		Task SaveAsync(string userName, TranslationSettings settings, CancellationToken cancellationToken = default);
	}
}
