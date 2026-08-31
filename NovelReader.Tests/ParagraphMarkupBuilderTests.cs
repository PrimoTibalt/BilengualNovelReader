using NovelReader.Domain.RealTimeReader.Reading;

namespace NovelReader.Tests
{
	public class ParagraphMarkupBuilderTests
	{
		private static IReadOnlySet<string> Known(params string[] terms) => new HashSet<string>(terms);

		// ---- Escaping: paragraph text is scraped and untrusted (D3). ----

		[Fact]
		public void Scraped_markup_is_inert_in_output()
		{
			string markup = ParagraphMarkupBuilder.BuildMarkup(
				"He said <script>alert('xss')</script> quietly.",
				Known());

			Assert.DoesNotContain("<script>", markup);
			Assert.Contains("&lt;script&gt;", markup);
		}

		[Fact]
		public void Scraped_markup_is_inert_even_while_wrapping_a_known_word()
		{
			string markup = ParagraphMarkupBuilder.BuildMarkup(
				"<img src=x onerror=alert(1)> ephemeral <b>bold</b>",
				Known("ephemeral"));

			Assert.DoesNotContain("<img", markup);
			Assert.DoesNotContain("<b>", markup);
			Assert.Contains("&lt;img", markup);
			// The only tags present are the ones the builder emitted itself.
			Assert.Contains("<span class=\"known-word\" data-term=\"ephemeral\">ephemeral</span>", markup);
		}

		[Fact]
		public void Quotes_in_text_cannot_break_out_of_the_data_attribute()
		{
			string markup = ParagraphMarkupBuilder.BuildMarkup(
				"a \"quoted\" word",
				Known("quoted"));

			Assert.DoesNotContain("\"quoted\"", markup[..markup.IndexOf("<span", StringComparison.Ordinal)]);
			Assert.Contains("&quot;", markup);
		}

		[Fact]
		public void Ampersands_are_escaped_once()
		{
			Assert.Equal("Fish &amp; Chips", ParagraphMarkupBuilder.BuildMarkup("Fish & Chips", Known()));
		}

		// ---- Wrapping ----

		[Fact]
		public void Known_word_is_wrapped_and_surrounding_text_preserved()
		{
			string markup = ParagraphMarkupBuilder.BuildMarkup("An ephemeral thing.", Known("ephemeral"));

			Assert.Equal(
				"An <span class=\"known-word\" data-term=\"ephemeral\">ephemeral</span> thing.",
				markup);
		}

		[Fact]
		public void Matching_ignores_case_and_trailing_punctuation()
		{
			string markup = ParagraphMarkupBuilder.BuildMarkup("\"Ephemeral,\" he said.", Known("ephemeral"));

			// The original surface form, punctuation and all, survives around the tag.
			Assert.Contains(">Ephemeral</span>", markup);
			Assert.Contains("&quot;", markup);
			Assert.Contains(",&quot; he said.", markup);
		}

		[Fact]
		public void Possessive_form_still_matches()
		{
			string markup = ParagraphMarkupBuilder.BuildMarkup("the Gu's power", Known("gu"));

			// The apostrophe in the surface form is escaped, as every scraped character is.
			Assert.Contains("data-term=\"gu\">Gu&#39;s</span>", markup);
		}

		[Fact]
		public void Unknown_words_are_left_alone()
		{
			Assert.Equal("An ephemeral thing.", ParagraphMarkupBuilder.BuildMarkup("An ephemeral thing.", Known("other")));
		}

		[Fact]
		public void Empty_vocabulary_still_escapes()
		{
			Assert.Equal("&lt;b&gt;", ParagraphMarkupBuilder.BuildMarkup("<b>", Known()));
		}

		// ---- Longest match (D4) ----

		[Fact]
		public void Longer_phrase_wins_over_its_first_word()
		{
			string markup = ParagraphMarkupBuilder.BuildMarkup("They give up now.", Known("give", "give up"));

			Assert.Contains("data-term=\"give up\">give up</span>", markup);
			Assert.DoesNotContain("data-term=\"give\">", markup);
		}

		[Fact]
		public void Single_word_matches_when_no_phrase_does()
		{
			string markup = ParagraphMarkupBuilder.BuildMarkup("They give in now.", Known("give", "give up"));
			Assert.Contains("data-term=\"give\">give</span>", markup);
		}

		[Fact]
		public void Multiple_occurrences_are_all_wrapped()
		{
			string markup = ParagraphMarkupBuilder.BuildMarkup("Gu and Gu and Gu.", Known("gu"));
			Assert.Equal(3, markup.Split("<span").Length - 1);
		}

		[Fact]
		public void Phrases_longer_than_the_cap_are_not_matched()
		{
			string tooLong = "one two three four five";
			string markup = ParagraphMarkupBuilder.BuildMarkup(tooLong, Known(tooLong));
			Assert.DoesNotContain("<span", markup);
		}

		[Fact]
		public void Empty_paragraph_yields_empty_markup()
		{
			Assert.Equal(string.Empty, ParagraphMarkupBuilder.BuildMarkup("", Known("gu")));
			Assert.Equal(string.Empty, ParagraphMarkupBuilder.BuildMarkup(null, Known("gu")));
		}
	}
}
