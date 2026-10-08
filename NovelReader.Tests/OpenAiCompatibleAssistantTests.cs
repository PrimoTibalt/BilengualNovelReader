using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NovelReader.Dictionary;
using NovelReader.Domain.RealTimeReader.Assistant;
using NovelReader.Domain.RealTimeReader.Definitions;

namespace NovelReader.Tests
{
	public class OpenAiCompatibleAssistantTests
	{
		private static readonly AssistantSettings Settings = new("digitalocean", "glm-5.3", "sk-do-1234567890");

		private static OpenAiCompatibleAssistant Assistant(HttpMessageHandler handler) =>
			new(new StubHttpClientFactory(handler, "https://unused.example/"), NullLogger<OpenAiCompatibleAssistant>.Instance);

		private static string Reply(string content, string finishReason = "stop") =>
			JsonSerializer.Serialize(new
			{
				choices = new[] { new { finish_reason = finishReason, message = new { role = "assistant", content } } }
			});

		[Fact]
		public async Task A_definition_is_read_from_json_wrapped_in_a_code_fence()
		{
			SequencedHttpMessageHandler handler = new((HttpStatusCode.OK, Reply("""
				```json
				{"senses":[{"partOfSpeech":"verb","text":"Smiled with contempt.","example":null},
				           {"partOfSpeech":"noun","text":"A contemptuous look.","example":"Her sneer said enough."}]}
				```
				""")));

			WordDefinition? definition = await Assistant(handler).DefineAsync(Settings, "sneered", "Fang Yuan sneered.");

			Assert.NotNull(definition);
			Assert.Equal("glm-5.3", definition.SourceName);
			Assert.Equal(2, definition.Senses.Count);
			Assert.Equal("Smiled with contempt.", definition.Senses[0].Text);
			Assert.Null(definition.Senses[0].Example);
			Assert.Equal("Her sneer said enough.", definition.Senses[1].Example);
		}

		[Fact]
		public async Task The_request_goes_to_the_chosen_service_with_the_key_and_the_paragraph()
		{
			SequencedHttpMessageHandler handler = new((HttpStatusCode.OK, Reply("насмехался")));

			await Assistant(handler).TranslateAsync(Settings, "sneered", "Fang Yuan sneered at the elders.", "Russian");

			HttpRequestMessage request = handler.Requests.Single();
			Assert.Equal("https://inference.do-ai.run/v1/chat/completions", request.RequestUri!.ToString());
			Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
			Assert.Equal("sk-do-1234567890", request.Headers.Authorization.Parameter);

			string body = handler.RequestBodies.Single();
			Assert.Contains("Fang Yuan sneered at the elders.", body);
			Assert.Contains("Russian", body);
			Assert.Contains("\"reasoning_effort\":\"none\"", body);
		}

		[Fact]
		public async Task The_body_is_sent_as_bare_json_with_no_charset_parameter()
		{
			// DigitalOcean's gateway refuses `application/json; charset=utf-8` with a 403.
			SequencedHttpMessageHandler handler = new((HttpStatusCode.OK, Reply("x")));

			await Assistant(handler).TranslateAsync(Settings, "sneered", null, "Russian");

			Assert.Equal("application/json", handler.Requests.Single().Content!.Headers.ContentType!.ToString());
		}

		[Fact]
		public async Task A_translation_loses_the_quotes_models_like_to_add()
		{
			SequencedHttpMessageHandler handler = new((HttpStatusCode.OK, Reply("  «насмехался»  ")));

			Assert.Equal("насмехался", await Assistant(handler).TranslateAsync(Settings, "sneered", null, "Russian"));
		}

		[Fact]
		public async Task A_model_that_refuses_reasoning_effort_is_asked_again_without_it_and_remembered()
		{
			SequencedHttpMessageHandler handler = new(
				(HttpStatusCode.BadRequest, """{"error":{"message":"Unrecognized request argument: reasoning_effort"}}"""),
				(HttpStatusCode.OK, Reply("first")),
				(HttpStatusCode.OK, Reply("second")));
			OpenAiCompatibleAssistant assistant = Assistant(handler);

			Assert.Equal("first", await assistant.TranslateAsync(Settings, "a", null, "Russian"));
			Assert.Equal("second", await assistant.TranslateAsync(Settings, "b", null, "Russian"));

			Assert.Equal(3, handler.Requests.Count);
			Assert.Contains("reasoning_effort", handler.RequestBodies[0]);
			Assert.DoesNotContain("reasoning_effort", handler.RequestBodies[1]);
			Assert.DoesNotContain("reasoning_effort", handler.RequestBodies[2]);
		}

		[Fact]
		public async Task A_wrong_key_is_no_answer_rather_than_an_exception()
		{
			SequencedHttpMessageHandler handler = new((HttpStatusCode.Unauthorized, """{"error":"bad key"}"""));

			Assert.Null(await Assistant(handler).DefineAsync(Settings, "sneered", null));
			Assert.Single(handler.Requests);
		}

		[Fact]
		public async Task A_service_that_is_down_is_no_answer()
		{
			Assert.Null(await Assistant(new FailingHttpMessageHandler()).TranslateAsync(Settings, "a", null, "Russian"));
		}

		[Fact]
		public void A_reply_cut_off_by_the_length_limit_is_refused()
		{
			Assert.Null(OpenAiCompatibleAssistant.ReadContent(Reply("{\"senses\":[{\"text\":\"half", "length")));
		}

		[Fact]
		public void Prose_instead_of_json_gives_no_senses()
		{
			Assert.Empty(OpenAiCompatibleAssistant.ReadSenses("It means to smile with contempt."));
		}
	}
}
