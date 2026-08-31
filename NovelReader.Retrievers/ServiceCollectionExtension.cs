using Microsoft.Extensions.DependencyInjection;
using NovelReader.Domain.RealTimeReader.Parsing;

namespace NovelReader.Retrievers
{
    public static class ServiceCollectionExtension
    {
        public static void RegisterHttpClientAndRetriever(this IServiceCollection services)
        {
            services.AddHttpClient(
                "NovelFire",
                client =>
                {
                    client.BaseAddress = new Uri("https://novelfire.net/");
                    client.DefaultRequestHeaders.UserAgent.Clear();
                    client.DefaultRequestHeaders.TryAddWithoutValidation(
                        "User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36"
                    );
                }
            );
            services.AddSingleton<IParagraphsRetriever, ParagraphsRetriever>();
            services.AddSingleton<ISearchNovelsRetriever, SearchNovelsRetriever>();
        }
    }
}
