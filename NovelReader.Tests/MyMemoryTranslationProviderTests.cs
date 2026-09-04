using System.Net;
using NovelReader.Dictionary;
using NovelReader.Domain.RealTimeReader.Translation;

namespace NovelReader.Tests
{
	public class MyMemoryTranslationProviderTests
	{
		private const string MyMemoryBase = "https://api.mymemory.translated.net/";

		private static MyMemoryTranslationProvider Provider(HttpMessageHandler handler) =>
			new(new StubHttpClientFactory(handler, MyMemoryBase));

		private static StubHttpMessageHandler Answering(string body, HttpStatusCode status = HttpStatusCode.OK) =>
			new(status, body);

		[Fact]
		public async Task A_translation_comes_back_with_the_language_it_was_asked_for()
		{
			StubHttpMessageHandler handler = Answering("""
				{ "responseData": { "translatedText": "опыт", "match": 1 }, "responseStatus": 200 }
				""");

			Translation? translation = await Provider(handler)
				.TranslateAsync("experience", "ru", "reader@example.com");

			Assert.NotNull(translation);
			Assert.Equal("опыт", translation.Text);
			Assert.Equal("ru", translation.TargetLanguage);
			Assert.Equal("MyMemory", translation.ProviderName);
		}

		[Fact]
		public async Task The_request_names_the_pair_and_carries_the_readers_email()
		{
			StubHttpMessageHandler handler = Answering("""
				{ "responseData": { "translatedText": "опыт" }, "responseStatus": 200 }
				""");

			await Provider(handler).TranslateAsync("experience", "ru", "reader@example.com");

			// Read back unescaped, so this asserts the pair and the address rather than which
			// of the two spellings of "|" the Uri class happened to keep.
			string requested = Uri.UnescapeDataString(handler.LastRequestUri!.ToString());
			Assert.Contains("q=experience", requested);
			Assert.Contains("langpair=en|ru", requested);
			Assert.Contains("de=reader@example.com", requested);
		}

		[Fact]
		public async Task The_out_of_allowance_warning_is_not_served_as_a_translation()
		{
			// MyMemory answers 200 and puts this where the translation goes. Showing it to the
			// reader as the answer is the junk-chapter mistake of D21, so it must be refused.
			StubHttpMessageHandler handler = Answering("""
				{ "responseData": { "translatedText": "MYMEMORY WARNING: YOU USED ALL AVAILABLE FREE TRANSLATIONS FOR TODAY" },
				  "responseStatus": 200 }
				""");

			Assert.Null(await Provider(handler).TranslateAsync("experience", "ru", "reader@example.com"));
		}

		[Fact]
		public async Task A_refusal_reported_inside_a_200_is_still_a_refusal()
		{
			StubHttpMessageHandler handler = Answering("""
				{ "responseData": { "translatedText": "" }, "responseDetails": "INVALID EMAIL PROVIDED", "responseStatus": 403 }
				""");

			Assert.Null(await Provider(handler).TranslateAsync("experience", "ru", "reader@example.com"));
		}

		[Fact]
		public async Task A_status_sent_as_a_string_is_read_the_same_as_a_number()
		{
			// The API is inconsistent about this field's type; both spellings mean success.
			StubHttpMessageHandler handler = Answering("""
				{ "responseData": { "translatedText": "опыт" }, "responseStatus": "200" }
				""");

			Assert.NotNull(await Provider(handler).TranslateAsync("experience", "ru", "reader@example.com"));
		}

		[Fact]
		public async Task Nonsense_and_outages_answer_null_rather_than_throwing()
		{
			Assert.Null(await Provider(Answering("not json at all")).TranslateAsync("experience", "ru", "reader@example.com"));
			Assert.Null(await Provider(new FailingHttpMessageHandler()).TranslateAsync("experience", "ru", "reader@example.com"));
			Assert.Null(await Provider(Answering("{}", HttpStatusCode.BadGateway)).TranslateAsync("experience", "ru", "reader@example.com"));
		}

		[Fact]
		public async Task Nothing_is_sent_for_an_empty_selection_or_one_too_long_to_be_accepted()
		{
			StubHttpMessageHandler handler = Answering("""
				{ "responseData": { "translatedText": "x" }, "responseStatus": 200 }
				""");
			MyMemoryTranslationProvider provider = Provider(handler);

			Assert.Null(await provider.TranslateAsync("   ", "ru", "reader@example.com"));
			Assert.Null(await provider.TranslateAsync(new string('a', 600), "ru", "reader@example.com"));
			Assert.Equal(0, handler.CallCount);
		}
	}
}
