using NovelReader.Domain.RealTimeReader.Assistant;
using NovelReader.Domain.RealTimeReader.Definitions;
using NovelReader.Domain.RealTimeReader.Translation;

namespace NovelReader.Tests
{
	/// <summary>Which of the reader's AI and the ordinary providers answers, and with what context (D35).</summary>
	public class AssistedLookupTests
	{
		private readonly InMemoryAssistantSettingsStore assistantStore = new();
		private readonly ScriptedAssistant assistant = new();
		private readonly InMemoryTranslationSettingsStore translationStore = new();
		private AssistantSettingsService AssistantSettings => new(assistantStore, new ReversingProtector());

		private async Task ConfigureAssistantAsync() =>
			await AssistantSettings.SaveAsync("anton", "digitalocean", "glm-5.3", "sk-do-1234567890");

		private TranslationService Translation(FixedTranslationProvider provider) =>
			new(translationStore, provider, AssistantSettings, assistant);

		[Fact]
		public async Task With_an_ai_set_up_it_translates_with_the_paragraph_into_the_stored_language()
		{
			await ConfigureAssistantAsync();
			translationStore.Settings = new TranslationSettings("anton@example.com", "ru");
			assistant.Translation = "насмехался";
			FixedTranslationProvider myMemory = new("усмехнулся");

			TranslationOutcome outcome = await Translation(myMemory).TranslateAsync("anton", "sneered", paragraph: "He sneered.");

			Assert.Equal("насмехался", outcome.Translation!.Text);
			Assert.Equal("glm-5.3", outcome.Translation.ProviderName);
			Assert.Equal("He sneered.", assistant.LastParagraph);
			Assert.Equal("Russian", assistant.LastLanguage);
			Assert.Equal(0, myMemory.Calls);
		}

		[Fact]
		public async Task When_the_ai_fails_the_ordinary_provider_still_answers()
		{
			await ConfigureAssistantAsync();
			translationStore.Settings = new TranslationSettings("anton@example.com", "ru");
			FixedTranslationProvider myMemory = new("усмехнулся");

			TranslationOutcome outcome = await Translation(myMemory).TranslateAsync("anton", "sneered");

			Assert.Equal("MyMemory", outcome.Translation!.ProviderName);
		}

		[Fact]
		public async Task Without_an_ai_the_ai_is_never_asked()
		{
			translationStore.Settings = new TranslationSettings("anton@example.com", "ru");

			await Translation(new FixedTranslationProvider("усмехнулся")).TranslateAsync("anton", "sneered", paragraph: "He sneered.");

			Assert.Equal(0, assistant.Calls);
		}

		[Fact]
		public async Task An_ai_with_no_language_to_translate_into_asks_for_settings()
		{
			await ConfigureAssistantAsync();

			TranslationOutcome outcome = await Translation(new FixedTranslationProvider("x")).TranslateAsync("anton", "sneered");

			Assert.Equal(TranslationFailure.NotConfigured, outcome.Failure);
		}

		[Fact]
		public async Task A_definition_comes_from_the_ai_and_never_reaches_the_shared_cache()
		{
			await ConfigureAssistantAsync();
			assistant.Definition = new WordDefinition
			{
				Term = "sneered",
				Senses = [new DefinitionSense { Text = "Smiled with contempt." }],
				SourceName = "glm-5.3"
			};
			RecordingDefinitionCache cache = new();
			AssistedDefinitionService service = new(AssistantSettings, assistant, new DefinitionLookupService(new NoDefinitionProvider(), cache));

			WordDefinition? definition = await service.LookUpAsync("anton", "sneered", "He sneered.");

			Assert.Equal("glm-5.3", definition!.SourceName);
			Assert.Equal("He sneered.", assistant.LastParagraph);
			Assert.Equal(0, cache.Writes);
		}

		[Fact]
		public async Task When_the_ai_has_no_definition_the_dictionary_answers()
		{
			await ConfigureAssistantAsync();
			RecordingDefinitionCache cache = new();
			AssistedDefinitionService service = new(AssistantSettings, assistant, new DefinitionLookupService(new NoDefinitionProvider(), cache));

			await service.LookUpAsync("anton", "sneered", "He sneered.");

			Assert.Equal(1, assistant.Calls);
			Assert.Equal(1, cache.Writes);
		}

		private sealed class NoDefinitionProvider : IDefinitionProvider
		{
			public string Name => "none";

			public Task<WordDefinition?> LookUpAsync(string term, CancellationToken cancellationToken = default) =>
				Task.FromResult<WordDefinition?>(null);
		}

		private sealed class RecordingDefinitionCache : IDefinitionCache
		{
			public int Writes { get; private set; }

			public Task<DefinitionLookupResult> TryGetAsync(string normalizedTerm, CancellationToken cancellationToken = default) =>
				Task.FromResult(DefinitionLookupResult.Unknown);

			public Task StoreAsync(WordDefinition definition, CancellationToken cancellationToken = default)
			{
				Writes++;
				return Task.CompletedTask;
			}

			public Task StoreMissAsync(string normalizedTerm, CancellationToken cancellationToken = default)
			{
				Writes++;
				return Task.CompletedTask;
			}
		}
	}
}
