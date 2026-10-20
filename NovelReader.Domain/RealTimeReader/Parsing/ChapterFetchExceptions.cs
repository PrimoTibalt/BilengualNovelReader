namespace NovelReader.Domain.RealTimeReader.Parsing
{
	/// <summary>
	/// The source site says there is no such chapter: past the end of what is published, or a
	/// number it never had (D49). Asking again will not change the answer.
	/// </summary>
	public sealed class ChapterNotFoundException(string message) : InvalidOperationException(message);

	/// <summary>
	/// The source site did not serve the chapter this time: it sent the request off to another
	/// site, answered with an empty page or an error, or the page was refused as not a chapter
	/// (D47). Worth asking again (D49).
	/// </summary>
	public sealed class ChapterUnavailableException(string message, Exception? innerException = null)
		: InvalidOperationException(message, innerException);
}
