namespace NovelReader.Domain.RealTimeReader.Parsing
{
	public interface IParagraphsRetriever
	{
		Task<Dictionary<int, string>> GetParagraphsAsync(string uriPath);
	}
}
