using System.Text.Json;
using NovelReader.Domain.RealTimeReader.Parsing;

namespace NovelReader.Retrievers
{
    public class SearchNovelsRetriever(IHttpClientFactory httpClientFactory)
        : ISearchNovelsRetriever
    {
        private static JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            // The catalogue sends "total_chapter". Case-insensitivity alone does not bridge
            // the underscore, so without this TotalChapter binds to nothing and every novel
            // comes back with a null chapter count.
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

        internal class NovelData
        {
            public string Title { get; set; }
            public string Slug { get; set; }
            public int? Rank { get; set; }
            public int? TotalChapter { get; set; }
            public string? Image { get; set; }
        }

        internal class NovelsSearchResponseData
        {
            public NovelData[] Data { get; init; }
        }

        public async Task<IReadOnlyCollection<NovelDataDto>> GetNovelsAsync(string uriPath)
        {
            HttpClient httpClient = httpClientFactory.CreateClient("NovelFire");
            HttpResponseMessage response = await httpClient.GetAsync(uriPath);
            response.EnsureSuccessStatusCode();

            object? boxedResponse = JsonSerializer.Deserialize(
                await response.Content.ReadAsStreamAsync(),
                typeof(NovelsSearchResponseData),
                _jsonOptions
            );
            if (boxedResponse is null)
            {
                throw new InvalidDataException("Unknown response structure");
            }

            NovelsSearchResponseData? responseData = boxedResponse as NovelsSearchResponseData;
            if (responseData?.Data is null)
            {
                // The endpoint answers {"data":null} when it does not like the request, and
                // {"data":[]} for a keyword that matches nothing. Neither is an error.
                return [];
            }

            List<NovelDataDto> result = new(responseData.Data.Length);
            foreach (var noveldata in responseData.Data)
            {
                result.Add(
                    new(noveldata.Title, noveldata.Slug, noveldata.Rank, noveldata.TotalChapter)
                );
            }
            return result;
        }
    }
}
