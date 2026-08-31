using Microsoft.Extensions.DependencyInjection;
using NovelReader.Domain.RealTimeReader.Definitions;

namespace NovelReader.Dictionary
{
	public static class ServiceCollectionExtension
	{
		/// <summary>
		/// Wikimedia asks clients to identify themselves; an anonymous agent risks being
		/// throttled.
		/// </summary>
		private const string UserAgent = "NovelReader/1.0 (self-hosted reading app)";

		/// <summary>
		/// Wiktionary answered in under a second in measurement. Anything slower than this
		/// is a bad day, and the reader should not wait for it.
		/// </summary>
		private static readonly TimeSpan PrimaryTimeout = TimeSpan.FromSeconds(5);

		/// <summary>
		/// The fallback has been seen at ~21 s. It is consulted only when the primary has
		/// already missed, and is cut off well before that.
		/// </summary>
		private static readonly TimeSpan FallbackTimeout = TimeSpan.FromSeconds(6);

		public static void RegisterDictionaryProviders(this IServiceCollection services)
		{
			services.AddHttpClient(WiktionaryDefinitionProvider.HttpClientName, client =>
			{
				client.BaseAddress = new Uri("https://en.wiktionary.org/api/rest_v1/page/definition/");
				client.Timeout = PrimaryTimeout;
				client.DefaultRequestHeaders.UserAgent.Clear();
				client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
			});

			services.AddHttpClient(FreeDictionaryDefinitionProvider.HttpClientName, client =>
			{
				client.BaseAddress = new Uri("https://api.dictionaryapi.dev/api/v2/entries/en/");
				client.Timeout = FallbackTimeout;
				client.DefaultRequestHeaders.UserAgent.Clear();
				client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
			});

			services.AddSingleton<WiktionaryDefinitionProvider>();
			services.AddSingleton<FreeDictionaryDefinitionProvider>();

			// Order is the fallback order. Registered as a factory so the concrete providers
			// are not themselves resolvable as IDefinitionProvider, which would recurse.
			services.AddSingleton<IDefinitionProvider>(serviceProvider => new FallbackDefinitionProvider(
			[
				serviceProvider.GetRequiredService<WiktionaryDefinitionProvider>(),
				serviceProvider.GetRequiredService<FreeDictionaryDefinitionProvider>()
			]));

			services.AddSingleton<DefinitionLookupService>();
		}
	}
}
