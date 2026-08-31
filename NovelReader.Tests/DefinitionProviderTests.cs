using System.Net;
using NovelReader.Dictionary;
using NovelReader.Domain.RealTimeReader.Definitions;

namespace NovelReader.Tests
{
	public class DefinitionProviderTests
	{
		private const string WiktionaryBase = "https://en.wiktionary.org/api/rest_v1/page/definition/";
		private const string FreeDictionaryBase = "https://api.dictionaryapi.dev/api/v2/entries/en/";

		// Shape mirrors the live response: definitions arrive as HTML fragments.
		private const string WiktionaryBody = """
		{
		  "en": [
		    {
		      "partOfSpeech": "Adjective",
		      "language": "English",
		      "definitions": [
		        { "definition": "Lasting for a <a rel=\"mw:WikiLink\" href=\"/wiki/short\">short</a> period of time.",
		          "examples": ["An <b>ephemeral</b> victory."] },
		        { "definition": "<span class=\"usage-label-sense\"></span> Existing for only one day." }
		      ]
		    }
		  ]
		}
		""";

		private const string FreeDictionaryBody = """
		[
		  { "word": "ephemeral",
		    "meanings": [
		      { "partOfSpeech": "noun",
		        "definitions": [ { "definition": "Something which lasts for a short period of time.", "example": "a mayfly is an ephemeral" } ] }
		    ] }
		]
		""";

		private static WiktionaryDefinitionProvider Wiktionary(HttpMessageHandler handler) =>
			new(new StubHttpClientFactory(handler, WiktionaryBase));

		private static FreeDictionaryDefinitionProvider FreeDictionary(HttpMessageHandler handler) =>
			new(new StubHttpClientFactory(handler, FreeDictionaryBase));

		[Fact]
		public async Task Wiktionary_parses_senses_and_strips_html()
		{
			WordDefinition? definition = await Wiktionary(
				new StubHttpMessageHandler(HttpStatusCode.OK, WiktionaryBody)).LookUpAsync("ephemeral");

			Assert.NotNull(definition);
			Assert.Equal(2, definition.Senses.Count);
			Assert.Equal("Lasting for a short period of time.", definition.Senses[0].Text);
			Assert.Equal("Adjective", definition.Senses[0].PartOfSpeech);
			Assert.Equal("An ephemeral victory.", definition.Senses[0].Example);
			Assert.DoesNotContain("<", definition.Senses[0].Text);
		}

		[Fact]
		public async Task Wiktionary_strips_leading_sense_label_markup()
		{
			WordDefinition? definition = await Wiktionary(
				new StubHttpMessageHandler(HttpStatusCode.OK, WiktionaryBody)).LookUpAsync("ephemeral");

			Assert.NotNull(definition);
			Assert.Equal("Existing for only one day.", definition.Senses[1].Text);
		}

		[Fact]
		public async Task Wiktionary_returns_null_for_unknown_word()
		{
			WordDefinition? definition = await Wiktionary(
				new StubHttpMessageHandler(HttpStatusCode.NotFound, "")).LookUpAsync("zzzxqq");

			Assert.Null(definition);
		}

		[Fact]
		public async Task Provider_returns_null_rather_than_throwing_when_the_service_is_down()
		{
			WordDefinition? definition = await Wiktionary(new FailingHttpMessageHandler()).LookUpAsync("ephemeral");
			Assert.Null(definition);
		}

		[Fact]
		public async Task Provider_returns_null_on_malformed_json()
		{
			WordDefinition? definition = await Wiktionary(
				new StubHttpMessageHandler(HttpStatusCode.OK, "{ not json")).LookUpAsync("ephemeral");

			Assert.Null(definition);
		}

		[Fact]
		public async Task FreeDictionary_parses_senses()
		{
			WordDefinition? definition = await FreeDictionary(
				new StubHttpMessageHandler(HttpStatusCode.OK, FreeDictionaryBody)).LookUpAsync("ephemeral");

			Assert.NotNull(definition);
			Assert.Single(definition.Senses);
			Assert.Equal("noun", definition.Senses[0].PartOfSpeech);
			Assert.Equal("a mayfly is an ephemeral", definition.Senses[0].Example);
		}

		[Fact]
		public async Task Phrases_are_url_encoded()
		{
			StubHttpMessageHandler handler = new(HttpStatusCode.OK, WiktionaryBody);
			await Wiktionary(handler).LookUpAsync("give up");

			Assert.NotNull(handler.LastRequestUri);

			// ToString() renders the unescaped form; AbsoluteUri is what goes on the wire.
			Assert.Contains("give%20up", handler.LastRequestUri.AbsoluteUri);
		}

		// ---- Fallback ordering (D1) ----

		[Fact]
		public async Task Fallback_uses_secondary_when_primary_misses()
		{
			FallbackDefinitionProvider fallback = new(
			[
				Wiktionary(new StubHttpMessageHandler(HttpStatusCode.NotFound, "")),
				FreeDictionary(new StubHttpMessageHandler(HttpStatusCode.OK, FreeDictionaryBody))
			]);

			WordDefinition? definition = await fallback.LookUpAsync("ephemeral");

			Assert.NotNull(definition);
			Assert.Equal("Free Dictionary", definition.SourceName);
		}

		[Fact]
		public async Task Fallback_uses_secondary_when_primary_is_down()
		{
			FallbackDefinitionProvider fallback = new(
			[
				Wiktionary(new FailingHttpMessageHandler()),
				FreeDictionary(new StubHttpMessageHandler(HttpStatusCode.OK, FreeDictionaryBody))
			]);

			Assert.NotNull(await fallback.LookUpAsync("ephemeral"));
		}

		[Fact]
		public async Task Fallback_does_not_consult_secondary_when_primary_answers()
		{
			StubHttpMessageHandler secondaryHandler = new(HttpStatusCode.OK, FreeDictionaryBody);
			FallbackDefinitionProvider fallback = new(
			[
				Wiktionary(new StubHttpMessageHandler(HttpStatusCode.OK, WiktionaryBody)),
				FreeDictionary(secondaryHandler)
			]);

			WordDefinition? definition = await fallback.LookUpAsync("ephemeral");

			Assert.Equal("Wiktionary", definition!.SourceName);
			Assert.Equal(0, secondaryHandler.CallCount);
		}

		[Fact]
		public async Task Fallback_returns_null_when_every_provider_misses()
		{
			FallbackDefinitionProvider fallback = new(
			[
				Wiktionary(new StubHttpMessageHandler(HttpStatusCode.NotFound, "")),
				FreeDictionary(new StubHttpMessageHandler(HttpStatusCode.NotFound, ""))
			]);

			Assert.Null(await fallback.LookUpAsync("zzzxqq"));
		}
	}
}
