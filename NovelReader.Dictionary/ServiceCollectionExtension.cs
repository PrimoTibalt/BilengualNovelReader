using Microsoft.Extensions.DependencyInjection;
using NovelReader.Domain.RealTimeReader.Assistant;
using NovelReader.Domain.RealTimeReader.Review;
using NovelReader.Domain.RealTimeReader.Definitions;
using NovelReader.Domain.RealTimeReader.Translation;

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

		/// <summary>
		/// The reader is watching a panel that says "translating…" while this runs, and the box
		/// gives up on its own after five seconds (D27) — so there is no point waiting longer.
		/// </summary>
		private static readonly TimeSpan TranslationTimeout = TimeSpan.FromSeconds(5);

		/// <summary>
		/// A model answering without reasoning takes a second or two. The box says
		/// `still looking…` after five (D33), so a slow model is waited for rather than dropped.
		/// </summary>
		private static readonly TimeSpan AssistantTimeout = TimeSpan.FromSeconds(30);

		/// <summary>
		/// Jev answers in about half a second. A judge that has not answered in three is not
		/// saving the reader anything; the reader's AI is asked instead.
		/// </summary>
		private static readonly TimeSpan JevTimeout = TimeSpan.FromSeconds(3);

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

			// Translation lives here too: it is the same kind of thing as a dictionary provider —
			// an outside language service reached over a named client with its own timeout (D31).
			services.AddHttpClient(MyMemoryTranslationProvider.HttpClientName, client =>
			{
				client.BaseAddress = new Uri("https://api.mymemory.translated.net/");
				client.Timeout = TranslationTimeout;
				client.DefaultRequestHeaders.UserAgent.Clear();
				client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
			});

			services.AddSingleton<ITranslationProvider, MyMemoryTranslationProvider>();
			services.AddSingleton<TranslationService>();

			// The reader's own AI (D35). No base address: each reader's calls go to the service
			// they picked, so the address travels with the request.
			services.AddHttpClient(OpenAiCompatibleAssistant.HttpClientName, client =>
			{
				client.Timeout = AssistantTimeout;
				client.DefaultRequestHeaders.UserAgent.Clear();
				client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
			});

			services.AddSingleton<IAssistant, OpenAiCompatibleAssistant>();
			services.AddSingleton<AssistedDefinitionService>();
			services.AddSingleton<ReviewTutor>();
		}

		/// <summary>
		/// The review's quick judge (D46), when the app has a Jev key. Without one nothing is
		/// registered and the reader's AI judges every answer, as before.
		/// </summary>
		public static void AddJevAnswerJudge(this IServiceCollection services, string? accessKey)
		{
			if (string.IsNullOrWhiteSpace(accessKey))
			{
				return;
			}

			services.AddHttpClient(JevAnswerJudge.HttpClientName, client =>
			{
				client.BaseAddress = new Uri("https://api.typesafe.ai/");
				client.Timeout = JevTimeout;
				client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessKey.Trim());
				client.DefaultRequestHeaders.UserAgent.Clear();
				client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
			});

			services.AddSingleton<IAnswerJudge, JevAnswerJudge>();
		}
	}
}
