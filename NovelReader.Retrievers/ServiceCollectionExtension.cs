using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using NovelReader.Domain.RealTimeReader.Parsing;
using System.Net;

namespace NovelReader.Retrievers
{
    public static class ServiceCollectionExtension
    {
        /// <summary>The named client for the site chapters and search come from.</summary>
        internal const string NovelSourceClient = "NovelSource";

        /// <summary>
        /// novelfire.net, until it began redirecting every chapter, by way of a "Loading..."
        /// page on readnovel.site, to this same site under a new name. The markup, the
        /// not-found page and the search endpoint are unchanged; only the chapter path went
        /// from <c>book/</c> to <c>novel/</c>. <c>NovelSource:BaseAddress</c> overrides it, so
        /// the next move is a setting.
        /// </summary>
        private const string DefaultBaseAddress = "https://novelphoenix.com/";

        public static void RegisterHttpClientAndRetriever(this IServiceCollection services, IConfiguration config)
        {
          ProxyOptions? proxyOptions = config.GetSection("ProxyOptions").Get<ProxyOptions>();
          string baseAddress = config["NovelSource:BaseAddress"] ?? DefaultBaseAddress;
          services.AddHttpClient(NovelSourceClient, 
              client =>
              {
              client.BaseAddress = new Uri(baseAddress);
              client.DefaultRequestHeaders.UserAgent.Clear();
              client.DefaultRequestHeaders.TryAddWithoutValidation(
                  "User-Agent",
                  "Mozilla/5.0 (X11; Linux x86_64; rv:155.0) Gecko/20100101 Firefox/155.0"
                  );
              })
          .ConfigurePrimaryHttpMessageHandler(() => {
              if (proxyOptions is null) {
              // Redirects are not followed: the site's random redirect to another site is a
              // chapter to ask for again, not a page to scrape (D49).
              return new HttpClientHandler {
              UseProxy = false,
              AllowAutoRedirect = false
              };
              }
              WebProxy proxyServer = new(proxyOptions.Url) {
              Credentials = new NetworkCredential(proxyOptions.Username, proxyOptions.Password)
              };
              HttpClientHandler handlerWithProxy = new() {
              Proxy = proxyServer,
              UseProxy = true,
              AllowAutoRedirect = false
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
