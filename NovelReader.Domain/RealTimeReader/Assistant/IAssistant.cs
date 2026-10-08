using NovelReader.Domain.RealTimeReader.Definitions;

namespace NovelReader.Domain.RealTimeReader.Assistant
{
	/// <summary>
	/// An AI that explains and translates a selection in the light of the paragraph it came
	/// from. Like the dictionary providers (D1), anything that goes wrong — a bad key, an
	/// unknown model, a timeout — is answered with null rather than thrown.
	/// </summary>
	public interface IAssistant
	{
		Task<WordDefinition?> DefineAsync(
			AssistantSettings settings,
			string selection,
			string? paragraph,
			CancellationToken cancellationToken = default);

		Task<string?> TranslateAsync(
			AssistantSettings settings,
			string selection,
			string? paragraph,
			string targetLanguageName,
			CancellationToken cancellationToken = default);
	}
}
