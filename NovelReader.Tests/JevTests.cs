using System.Globalization;
using NovelReader.Dictionary;
using NovelReader.Domain.RealTimeReader.Assistant;
using NovelReader.Domain.RealTimeReader.Books;
using NovelReader.Domain.RealTimeReader.Definitions;
using NovelReader.Domain.RealTimeReader.Parsing;
using NovelReader.Domain.RealTimeReader.Reading;
using NovelReader.Domain.RealTimeReader.Review;

namespace NovelReader.Tests
{
	/// <summary>Reading Jev's answers, and what each of its callers makes of them (D46, D47).</summary>
	public class JevTests
	{
		private static string Choice(string option, double probability, double confidence) =>
			"{\"model\":\"jev-1.13.0\",\"answers\":{\"q\":{\"type\":\"choice\",\"choice\":\"" + option
			+ "\",\"confidence\":" + Number(confidence) + ",\"probabilities\":{\"" + option + "\":" + Number(probability)
			+ "}}},\"usage\":{\"input_tokens\":120,\"output_tokens\":0}}";

		private static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);

		[Fact]
		public void A_choice_carries_the_probability_given_to_it_not_the_lower_confidence()
		{
			Assert.Equal(new JevChoice("correct", 0.53), JevClient.ReadChoice(Choice("correct", 0.53, 0.30)));
		}

		[Fact]
		public void Without_probabilities_the_confidence_stands_in()
		{
			Assert.Equal(
				new JevChoice("wrong", 0.9),
				JevClient.ReadChoice("""{"answers":{"q":{"type":"choice","choice":"wrong","confidence":0.9}}}"""));
		}

		[Theory]
		[InlineData("""{"answers":{"q":{"choice":"wrong"}}}""")]
		[InlineData("""{"answers":{}}""")]
		[InlineData("""{"answers":[]}""")]
		[InlineData("not json")]
		public void Anything_else_is_no_choice(string body)
		{
			Assert.Null(JevClient.ReadChoice(body));
		}

		[Fact]
		public void A_yes_or_no_answer_is_the_probability_of_yes()
		{
			Assert.Equal(0.04, JevClient.ReadYesProbability("""{"answers":{"q":{"type":"noul","noul":0.04}}}"""));
			Assert.Null(JevClient.ReadYesProbability("""{"answers":{"q":{"type":"noul"}}}"""));
		}

		[Theory]
		[InlineData("correct", 0.9, AnswerVerdict.Correct)]
		[InlineData("partial", 0.6, AnswerVerdict.Partial)]
		[InlineData("wrong", 1.0, AnswerVerdict.Wrong)]
		public void A_confident_choice_is_the_verdict(string option, double probability, AnswerVerdict verdict)
		{
			Assert.Equal(verdict, JevAnswerJudge.VerdictFor(new JevChoice(option, probability)));
		}

		[Fact]
		public void An_unsure_or_unknown_verdict_is_left_to_the_ai()
		{
			Assert.Null(JevAnswerJudge.VerdictFor(new JevChoice("correct", 0.53)));
			Assert.Null(JevAnswerJudge.VerdictFor(new JevChoice("maybe", 0.99)));
			Assert.Null(JevAnswerJudge.VerdictFor(null));
		}

		[Fact]
		public void A_sense_is_picked_by_its_number_from_one()
		{
			Assert.Equal(15, JevSenseChooser.IndexFor(new JevChoice("16", 0.98), senseCount: 42));
		}

		[Theory]
		[InlineData("none", 0.99)]
		[InlineData("43", 0.99)]
		[InlineData("0", 0.99)]
		[InlineData("16", 0.4)]
		public void None_an_unknown_number_or_an_unsure_pick_keeps_the_dictionary_order(string option, double probability)
		{
			Assert.Null(JevSenseChooser.IndexFor(new JevChoice(option, probability), senseCount: 42));
		}

		private static JevBookChapterClassifier BookClassifier(HttpMessageHandler handler) =>
			new(new JevClient(
				new StubHttpClientFactory(handler, "https://api.typesafe.ai/"),
				Microsoft.Extensions.Logging.Abstractions.NullLogger<JevClient>.Instance));

		private static readonly BookChapterSample Afterword =
			new("Re:ZERO Vol. 26", 12, 13, "Afterword", "Hello! This is Tappei Nagatsuki!", 0);

		[Theory]
		[InlineData(0.01, true)]
		[InlineData(0.19, true)]
		[InlineData(0.2, false)]
		[InlineData(0.61, false)]
		[InlineData(0.98, false)]
		public async Task A_book_chapter_is_hidden_only_when_jev_is_sure_it_is_not_the_book(double story, bool extra)
		{
			StubHttpMessageHandler handler = new(System.Net.HttpStatusCode.OK,
				"{\"answers\":{\"q\":{\"type\":\"noul\",\"noul\":" + Number(story) + "}}}");

			Assert.Equal(extra, await BookClassifier(handler).IsExtraAsync(Afterword));
			Assert.Equal(1, handler.CallCount);
		}

		[Fact]
		public async Task A_book_chapter_jev_cannot_judge_has_no_answer_so_the_book_is_asked_about_again()
		{
			Assert.Null(await BookClassifier(new FailingHttpMessageHandler()).IsExtraAsync(Afterword));
			Assert.Null(await BookClassifier(new StubHttpMessageHandler(System.Net.HttpStatusCode.TooManyRequests, "")).IsExtraAsync(Afterword));
		}

		[Fact]
		public void A_book_chapter_is_described_by_its_place_title_and_opening_or_its_pictures()
		{
			Assert.Equal(
				"Book: Re:ZERO Vol. 26\nSection 12 of 13: Afterword\nIt begins:\nHello! This is Tappei Nagatsuki!",
				JevBookChapterClassifier.Describe(Afterword).ReplaceLineEndings("\n"));
			Assert.EndsWith(
				"Section 3 of 13: Title Page\nNo text: 1 picture.",
				JevBookChapterClassifier.Describe(new("Re:ZERO Vol. 26", 3, 13, "Title Page", "", 1)).ReplaceLineEndings("\n"));
		}

		[Fact]
		public void A_chapter_is_judged_by_its_opening_paragraphs_in_order()
		{
			Dictionary<int, string> paragraphs = new() { [2] = " second ", [1] = "Chapter 1" };
			for (int number = 3; number <= 40; number++)
			{
				paragraphs[number] = $"paragraph {number}";
			}

			string opening = JevChapterTextCheck.Opening(paragraphs);

			Assert.StartsWith("Chapter 1\nsecond\nparagraph 3", opening.ReplaceLineEndings("\n"));
			Assert.Contains("paragraph 20", opening);
			Assert.DoesNotContain("paragraph 21", opening);
		}
	}

	/// <summary>The dictionary's senses, put in the order the paragraph calls for (D47).</summary>
	public class SenseInContextTests
	{
		private static readonly DefinitionSense[] BankSenses =
		[
			.. Enumerable.Range(1, 15).Select(number => new DefinitionSense { PartOfSpeech = "Noun", Text = $"money sense {number}" }),
			new DefinitionSense { PartOfSpeech = "Noun", Text = "An edge of river, lake, or other watercourse." },
			.. Enumerable.Range(17, 26).Select(number => new DefinitionSense { PartOfSpeech = "Noun", Text = $"other sense {number}" })
		];

		private static AssistedDefinitionService Service(ISenseChooser? chooser) =>
			new(
				new AssistantSettingsService(new InMemoryAssistantSettingsStore(), new ReversingProtector()),
				new ScriptedAssistant(),
				new DefinitionLookupService(new FixedProvider(BankSenses), new NoCache()),
				chooser);

		[Fact]
		public async Task The_sense_the_paragraph_uses_comes_first_and_the_reader_sees_a_dozen()
		{
			RecordingChooser chooser = new(15);

			WordDefinition? definition = await Service(chooser).LookUpAsync("anton", "bank", "They pulled the boat up onto the bank.");

			Assert.Equal("An edge of river, lake, or other watercourse.", definition!.Senses[0].Text);
			Assert.Equal("money sense 1", definition.Senses[1].Text);
			Assert.Equal(AssistedDefinitionService.MaximumShownSenses, definition.Senses.Count);
			Assert.Equal(42, chooser.OfferedSenses);
		}

		[Fact]
		public async Task Without_a_paragraph_or_a_pick_the_dictionary_order_stands()
		{
			RecordingChooser chooser = new(null);

			WordDefinition? withoutParagraph = await Service(chooser).LookUpAsync("anton", "bank", paragraph: null);
			WordDefinition? withoutPick = await Service(chooser).LookUpAsync("anton", "bank", "Some paragraph.");
			WordDefinition? withoutChooser = await Service(null).LookUpAsync("anton", "bank", "Some paragraph.");

			Assert.Equal(1, chooser.Calls);
			Assert.All([withoutParagraph, withoutPick, withoutChooser], definition => Assert.Equal("money sense 1", definition!.Senses[0].Text));
			Assert.All([withoutParagraph, withoutPick, withoutChooser], definition => Assert.Equal(12, definition!.Senses.Count));
		}

		private sealed class RecordingChooser(int? pick) : ISenseChooser
		{
			public int Calls { get; private set; }
			public int OfferedSenses { get; private set; }

			public Task<int?> ChooseAsync(string term, string paragraph, IReadOnlyList<DefinitionSense> senses, CancellationToken cancellationToken = default)
			{
				Calls++;
				OfferedSenses = senses.Count;
				return Task.FromResult(pick);
			}
		}

		private sealed class FixedProvider(IReadOnlyList<DefinitionSense> senses) : IDefinitionProvider
		{
			public string Name => "fixed";

			public Task<WordDefinition?> LookUpAsync(string term, CancellationToken cancellationToken = default) =>
				Task.FromResult<WordDefinition?>(new WordDefinition { Term = term, Senses = senses, SourceName = Name });
		}

		private sealed class NoCache : IDefinitionCache
		{
			public Task<DefinitionLookupResult> TryGetAsync(string normalizedTerm, CancellationToken cancellationToken = default) =>
				Task.FromResult(DefinitionLookupResult.Unknown);

			public Task StoreAsync(WordDefinition definition, CancellationToken cancellationToken = default) => Task.CompletedTask;

			public Task StoreMissAsync(string normalizedTerm, CancellationToken cancellationToken = default) => Task.CompletedTask;
		}
	}

	/// <summary>A scraped page that is not a chapter is never stored as one (D47).</summary>
	public class ChapterTextCheckTests
	{
		private readonly CountingParagraphsRetriever retriever = new();

		private ChapterPreparationService Service(bool sitePage) =>
			new(new FakeNovelRepository(new FakeCollectionOfChapters()), retriever, new EmptyVocabularyRepository(), new FixedCheck(sitePage))
			{
				RetryDelays = [TimeSpan.Zero]
			};

		[Fact]
		public async Task A_site_page_is_refused_retried_and_scraped_again_next_time()
		{
			ChapterPreparationService service = Service(sitePage: true);

			await Assert.ThrowsAsync<ChapterUnavailableException>(() => service.PrepareAsync("anton", "reverend-insanity", 7));
			await Assert.ThrowsAsync<ChapterUnavailableException>(() => service.PrepareAsync("anton", "reverend-insanity", 7));

			// Each load tries twice (one retry), and nothing was stored between them.
			Assert.Equal(4, retriever.CallsForChapter(7));
		}

		[Fact]
		public async Task A_chapter_is_stored_and_served_from_the_store()
		{
			ChapterPreparationService service = Service(sitePage: false);

			PreparedChapter first = await service.PrepareAsync("anton", "reverend-insanity", 7);
			await service.PrepareAsync("anton", "reverend-insanity", 7);

			Assert.Equal(10, first.Paragraphs.Count);
			Assert.Equal(1, retriever.CallsForChapter(7));
		}

		private sealed class FixedCheck(bool sitePage) : IChapterTextCheck
		{
			public Task<bool> IsSitePageAsync(IReadOnlyDictionary<int, string> paragraphs, CancellationToken cancellationToken = default) =>
				Task.FromResult(sitePage);
		}
	}
}
