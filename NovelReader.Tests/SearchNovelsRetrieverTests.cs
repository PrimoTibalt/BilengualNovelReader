using System.Net;
using NovelReader.Domain.RealTimeReader.Parsing;
using NovelReader.Retrievers;

namespace NovelReader.Tests
{
	/// <summary>
	/// Reading the catalogue's JSON. The field the reader actually cares about is
	/// <c>total_chapter</c>, whose snake_case is the whole reason a naming policy is set (D22).
	/// </summary>
	public class SearchNovelsRetrieverTests
	{
		private static SearchNovelsRetriever Build(string json, HttpStatusCode status = HttpStatusCode.OK)
		{
			return new SearchNovelsRetriever(
				new StubHttpClientFactory(new StubHttpMessageHandler(status, json), "https://example.test/"));
		}

		[Fact]
		public async Task Every_field_is_read_including_the_snake_cased_one()
		{
			SearchNovelsRetriever retriever = Build(
				"""{"data":[{"title":"Reverend Insanity","slug":"reverend-insanity","rank":3,"total_chapter":2334,"image":"x.jpg"}]}""");

			NovelDataDto novel = (await retriever.GetNovelsAsync("ajax/searchLive?keyword=x")).Single();

			Assert.Equal("Reverend Insanity", novel.Title);
			Assert.Equal("reverend-insanity", novel.Slug);
			Assert.Equal(3, novel.Rank);
			// Binds only because of the snake-case naming policy; it was null without it.
			Assert.Equal(2334, novel.TotalChapter);
		}

		[Fact]
		public async Task A_keyword_that_matches_nothing_is_an_empty_list()
		{
			SearchNovelsRetriever retriever = Build("""{"data":[]}""");

			Assert.Empty(await retriever.GetNovelsAsync("ajax/searchLive?keyword=x"));
		}

		[Fact]
		public async Task Null_data_is_an_empty_list_not_a_crash()
		{
			// What the endpoint answers when it does not like the request.
			SearchNovelsRetriever retriever = Build("""{"data":null}""");

			Assert.Empty(await retriever.GetNovelsAsync("ajax/searchLive?keyword=x"));
		}

		[Fact]
		public async Task Missing_optional_fields_are_null_rather_than_zero()
		{
			SearchNovelsRetriever retriever = Build("""{"data":[{"title":"A","slug":"a"}]}""");

			NovelDataDto novel = (await retriever.GetNovelsAsync("ajax/searchLive?keyword=x")).Single();

			Assert.Null(novel.Rank);
			Assert.Null(novel.TotalChapter);
		}

		[Fact]
		public async Task A_failed_request_is_not_swallowed()
		{
			SearchNovelsRetriever retriever = Build("""{"data":[]}""", HttpStatusCode.TooManyRequests);

			await Assert.ThrowsAsync<HttpRequestException>(
				() => retriever.GetNovelsAsync("ajax/searchLive?keyword=x"));
		}
	}
}
