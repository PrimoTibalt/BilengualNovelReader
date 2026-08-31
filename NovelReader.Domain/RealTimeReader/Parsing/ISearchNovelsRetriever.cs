namespace NovelReader.Domain.RealTimeReader.Parsing
{
    public interface ISearchNovelsRetriever
    {
        Task<IReadOnlyCollection<NovelDataDto>> GetNovelsAsync(string uriPath);
    }
}
