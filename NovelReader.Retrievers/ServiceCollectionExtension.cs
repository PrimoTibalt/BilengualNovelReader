using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using NovelReader.Domain.RealTimeReader.Parsing;
using System.Net;

namespace NovelReader.Retrievers
{
    public static class ServiceCollectionExtension
    {
        public static void RegisterHttpClientAndRetriever(this IServiceCollection services, IConfiguration config)
        {
          ProxyOptions? proxyOptions = config.GetSection("ProxyOptions").Get<ProxyOptions>();
          services.AddHttpClient("NovelFire", 
              client =>
              {
              client.BaseAddress = new Uri("https://novelfire.net/");
              client.DefaultRequestHeaders.UserAgent.Clear();
              client.DefaultRequestHeaders.TryAddWithoutValidation(
                  "User-Agent",
                  "Mozilla/5.0 (X11; Linux x86_64; rv:155.0) Gecko/20100101 Firefox/155.0"
                  );
              })
          .ConfigurePrimaryHttpMessageHandler(() => {
              if (proxyOptions is null) {
              return new HttpClientHandler {
              UseProxy = false
              };
              }
              WebProxy proxyServer = new(proxyOptions.Url) {
              Credentials = new NetworkCredential(proxyOptions.Username, proxyOptions.Password)
              };
              HttpClientHandler handlerWithProxy = new() {
              Proxy = proxyServer,
              UseProxy = true,
              };
              return handlerWithProxy;
              }
              );
            services.AddSingleton<IParagraphsRetriever, ParagraphsRetriever>();
            services.AddSingleton<ISearchNovelsRetriever, SearchNovelsRetriever>();
        }

        private class ProxyOptions {
          public string Url { get; set; } = "";
          public string Username { get; set; } = "";
          public string Password { get; set; } = "";
        }
    }
}
