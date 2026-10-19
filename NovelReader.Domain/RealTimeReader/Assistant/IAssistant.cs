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

		/// <summary>
		/// One instruction and one question, answered as plain text — for callers that write their
		/// own prompt and read the answer themselves, as the review does. Null when the AI gave no
		/// usable answer.
		/// </summary>
		Task<string?> AskAsync(
			AssistantSettings settings,
			string instructions,
			string question,
			CancellationToken cancellationToken = default);
	}
}
