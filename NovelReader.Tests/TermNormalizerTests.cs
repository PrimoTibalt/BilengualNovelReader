using NovelReader.Domain.RealTimeReader.Vocabulary;

namespace NovelReader.Tests
{
	public class TermNormalizerTests
	{
		[Theory]
		[InlineData("Ephemeral", "ephemeral")]
		[InlineData("EPHEMERAL", "ephemeral")]
		[InlineData("ephemeral", "ephemeral")]
		public void Normalize_lowercases(string input, string expected)
		{
			Assert.Equal(expected, TermNormalizer.Normalize(input));
		}

		[Theory]
		[InlineData("\"Ephemeral,\"", "ephemeral")]
		[InlineData("(ephemeral)", "ephemeral")]
		[InlineData("ephemeral—", "ephemeral")]
		[InlineData("...ephemeral!?", "ephemeral")]
		public void Normalize_strips_surrounding_punctuation(string input, string expected)
		{
			Assert.Equal(expected, TermNormalizer.Normalize(input));
		}

		[Theory]
		[InlineData("ephemeral's", "ephemeral")]
		[InlineData("ephemeral’s", "ephemeral")]
		[InlineData("Gu's", "gu")]
		public void Normalize_strips_possessives(string input, string expected)
		{
			Assert.Equal(expected, TermNormalizer.Normalize(input));
		}

		[Theory]
		[InlineData("don't", "don't")]
		[InlineData("self-aware", "self-aware")]
		public void Normalize_keeps_interior_joiners(string input, string expected)
		{
			Assert.Equal(expected, TermNormalizer.Normalize(input));
		}

		[Fact]
		public void Normalize_collapses_phrase_whitespace()
		{
			Assert.Equal("give up", TermNormalizer.Normalize("  Give   Up.  "));
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("   ")]
		[InlineData("...")]
		[InlineData("—")]
		public void Normalize_returns_empty_for_nothing_meaningful(string? input)
		{
			Assert.Equal(string.Empty, TermNormalizer.Normalize(input));
		}

		[Fact]
		public void Normalize_does_not_strip_a_plural_that_is_not_possessive()
		{
			Assert.Equal("immortals", TermNormalizer.Normalize("Immortals"));
		}
	}
}
