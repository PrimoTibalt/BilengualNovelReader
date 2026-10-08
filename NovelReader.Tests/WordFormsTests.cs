using NovelReader.Domain.RealTimeReader.Definitions;
using NovelReader.Domain.RealTimeReader.Reading;
using NovelReader.Domain.RealTimeReader.Vocabulary;

namespace NovelReader.Tests
{
	/// <summary>Matching a saved word in all its forms, and no others (D39).</summary>
	public class WordFormsTests
	{
		private static WordDefinition Defined(params string[] senses) => new()
		{
			Term = "x",
			Senses = [.. senses.Select(text => new DefinitionSense { Text = text })],
			SourceName = "Wiktionary"
		};

		[Theory]
		[InlineData("sneer", "sneers sneered sneering")]
		[InlineData("hope", "hopes hoped hoping")]
		[InlineData("stop", "stops stopped stopping")]
		[InlineData("try", "tries tried trying")]
		[InlineData("die", "dies died dying")]
		[InlineData("agree", "agrees agreed agreeing")]
		[InlineData("watch", "watches watched watching")]
		[InlineData("panic", "panics panicked panicking")]
		[InlineData("run", "runs ran running")]
		[InlineData("go", "goes went gone going")]
		[InlineData("see", "sees saw seen seeing")]
		[InlineData("child", "children")]
		[InlineData("knife", "knives")]
		[InlineData("happy", "happier happiest")]
		[InlineData("visit", "visited visiting")]
		[InlineData("admit", "admitted admitting")]
		[InlineData("give up", "gave up giving up gives up given up")]
		public void A_base_form_generates_its_inflections(string baseForm, string expected)
		{
			IReadOnlyCollection<string> forms = WordForms.FormsOf(baseForm);

			foreach (string form in expected.Split(' ').Chunk(baseForm.Contains(' ') ? 2 : 1).Select(words => string.Join(' ', words)))
			{
				Assert.Contains(form, forms);
			}
		}

		[Theory]
		// The ones a stemmer or careless doubling would get wrong.
		[InlineData("hope", "hopping")]
		[InlineData("hop", "hoping")]
		[InlineData("be", "bed")]
		[InlineData("see", "seed")]
		[InlineData("corn", "corner")]
		[InlineData("show", "shower")]
		[InlineData("sum", "summer")]
		[InlineData("general", "generous")]
		[InlineData("sing", "singed")]
		public void A_base_form_does_not_reach_unrelated_words(string baseForm, string unrelated)
		{
			Assert.DoesNotContain(unrelated, WordForms.FormsOf(baseForm));
		}

		[Theory]
		[InlineData("sneered", "sneer")]
		[InlineData("hoping", "hope")]
		[InlineData("hopping", "hop")]
		[InlineData("stopped", "stop")]
		[InlineData("tried", "try")]
		[InlineData("dying", "die")]
		[InlineData("playing", "play")]
		[InlineData("agreed", "agree")]
		[InlineData("watches", "watch")]
		[InlineData("ran", "run")]
		[InlineData("children", "child")]
		[InlineData("lay", "lie")]
		[InlineData("giving up", "give up")]
		public void With_no_dictionary_entry_the_rules_find_the_base(string word, string expected)
		{
			Assert.Contains(expected, WordForms.CandidateBasesOf(word));
		}

		[Theory]
		[InlineData("hoping", "hop")]
		[InlineData("hopping", "hope")]
		[InlineData("sing", "s")]
		public void The_rules_do_not_find_a_base_that_does_not_produce_the_word(string word, string wrong)
		{
			Assert.DoesNotContain(wrong, WordForms.CandidateBasesOf(word));
		}

		[Fact]
		public void Words_no_dictionary_knew_match_through_every_candidate_base()
		{
			// `debated` could be debat-ed or debate-d; matching through both reaches `debates`
			// and `debating`, and the junk `debat` forms never occur in text.
			VocabularyForms forms = new([Entry("debated", baseForm: null)]);

			Assert.Equal("debated", forms.SavedTermFor("debates"));
			Assert.Equal("debated", forms.SavedTermFor("debating"));
			Assert.Equal("debated", forms.SavedTermFor("debate"));
		}

		[Fact]
		public void The_dictionarys_inflection_note_names_the_base()
		{
			WordDefinition sneered = Defined("simple past and past participle of sneer");

			Assert.Equal("sneer", WordForms.BaseFromDictionary("sneered", sneered));
		}

		[Fact]
		public void A_dictionary_entry_with_no_inflection_note_means_the_word_is_a_base()
		{
			// The rules alone would make `seed` the past of `see`.
			WordDefinition seed = Defined("The ovule of a flowering plant.");

			Assert.Equal("seed", WordForms.BaseFromDictionary("seed", seed));
		}

		[Fact]
		public void An_inflection_note_naming_a_word_that_does_not_produce_it_is_ignored()
		{
			WordDefinition odd = Defined("Alternative form of colour");

			Assert.Equal("color", WordForms.BaseFromDictionary("color", odd));
		}

		[Fact]
		public void An_inflection_note_below_a_noun_does_not_count()
		{
			// Wiktionary lists `evening` the noun first and "present participle of even" later;
			// taking that would underline every "even" in the novel.
			WordDefinition evening = new()
			{
				Term = "evening",
				SourceName = "Wiktionary",
				Senses =
				[
					new DefinitionSense { PartOfSpeech = "Noun", Text = "The time of day between afternoon and night." },
					new DefinitionSense { PartOfSpeech = "Verb", Text = "present participle and gerund of even" }
				]
			};

			Assert.Equal("evening", WordForms.BaseFromDictionary("evening", evening));
		}

		[Fact]
		public void An_adjective_first_still_takes_the_verb_its_note_names()
		{
			// `sneering` the adjective means what `sneer` means; it should match `sneered` too.
			WordDefinition sneering = new()
			{
				Term = "sneering",
				SourceName = "Wiktionary",
				Senses =
				[
					new DefinitionSense { PartOfSpeech = "Adjective", Text = "Expressing contempt or scorn." },
					new DefinitionSense { PartOfSpeech = "Verb", Text = "present participle and gerund of sneer" }
				]
			};

			Assert.Equal("sneer", WordForms.BaseFromDictionary("sneering", sneering));
		}

		[Fact]
		public void A_code_or_abbreviation_before_the_word_is_passed_over()
		{
			// Wiktionary's real entry for `ran`, in its order.
			WordDefinition ran = new()
			{
				Term = "ran",
				SourceName = "Wiktionary",
				Senses =
				[
					new DefinitionSense { PartOfSpeech = "Symbol", Text = "ISO 639-3 language code for Riantana." },
					new DefinitionSense { PartOfSpeech = "Verb", Text = "simple past of run" },
					new DefinitionSense { PartOfSpeech = "Noun", Text = "Yarns coiled on a spun-yarn winch." }
				]
			};

			Assert.Equal("run", WordForms.BaseFromDictionary("ran", ran));
		}

		[Fact]
		public void No_dictionary_entry_means_no_dictionary_base()
		{
			Assert.Null(WordForms.BaseFromDictionary("sneered", null));
		}

		[Fact]
		public void Every_form_points_back_to_the_word_as_saved()
		{
			VocabularyForms forms = new([Entry("sneered", baseForm: "sneer")]);

			Assert.Equal("sneered", forms.SavedTermFor("sneering"));
			Assert.Equal("sneered", forms.SavedTermFor("sneer"));
			Assert.Null(forms.SavedTermFor("sneak"));
		}

		[Fact]
		public void A_word_saved_as_itself_wins_over_one_that_generates_it()
		{
			VocabularyForms forms = new([Entry("run", "run"), Entry("running", "running")]);

			Assert.Equal("running", forms.SavedTermFor("running"));
			Assert.Equal("run", forms.SavedTermFor("ran"));
		}

		[Fact]
		public void Words_saved_before_base_forms_still_match_their_forms()
		{
			VocabularyForms forms = new([Entry("stopped", baseForm: null)]);

			Assert.Equal("stopped", forms.SavedTermFor("stopping"));
		}

		[Fact]
		public void The_underline_lands_on_the_text_and_carries_the_saved_word()
		{
			VocabularyForms forms = new([Entry("run", "run")]);

			string markup = ParagraphMarkupBuilder.BuildMarkup("He was running, then he ran.", forms.SavedTermByForm);

			Assert.Equal(
				"He was <span class=\"known-word\" data-term=\"run\">running</span>, then he " +
				"<span class=\"known-word\" data-term=\"run\">ran</span>.",
				markup);
		}

		private static VocabularyEntry Entry(string term, string? baseForm) => new()
		{
			NormalizedTerm = term,
			BaseForm = baseForm,
			SurfaceForm = term,
			NovelName = "novel",
			SavedAtUtc = DateTime.UnixEpoch
		};
	}
}
