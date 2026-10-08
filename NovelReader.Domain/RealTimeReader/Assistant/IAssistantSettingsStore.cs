namespace NovelReader.Domain.RealTimeReader.Assistant
{
	/// <summary>
	/// Where a reader's AI settings live. All three fields are written and cleared together, so
	/// a half-configured row cannot happen.
	/// </summary>
	public interface IAssistantSettingsStore
	{
		/// <summary>Null when this reader has not set an AI up, or has removed it.</summary>
		Task<StoredAssistantSettings?> GetAsync(string userName, CancellationToken cancellationToken = default);

		Task SaveAsync(string userName, StoredAssistantSettings settings, CancellationToken cancellationToken = default);

		Task RemoveAsync(string userName, CancellationToken cancellationToken = default);
	}
}
