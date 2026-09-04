namespace NovelReader.Domain.RealTimeReader.Translation
{
	/// <summary>One translated string, and the language it was translated into.</summary>
	public sealed record Translation(string Text, string TargetLanguage, string ProviderName);
}
