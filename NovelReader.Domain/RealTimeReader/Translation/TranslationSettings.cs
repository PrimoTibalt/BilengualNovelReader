namespace NovelReader.Domain.RealTimeReader.Translation
{
	/// <summary>
	/// What a reader has to tell us before a translation can be fetched: where to send it and
	/// who is asking.
	///
	/// The email is not an account and not a login — MyMemory raises a caller's daily
	/// allowance tenfold when a request carries one, and asks for nothing else in return
	/// (D31). It is stored per reader because it is the reader's allowance being spent.
	/// </summary>
	public sealed record TranslationSettings(string Email, string TargetLanguage);
}
