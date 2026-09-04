using NovelReader.Domain.RealTimeReader.Translation;

namespace NovelReader.Tests
{
	public class TranslationSettingsValidatorTests
	{
		[Theory]
		[InlineData("")]
		[InlineData("   ")]
		[InlineData("notanemail")]
		[InlineData("no@domain")]           // no dot after the @
		[InlineData("two@@ats.com")]
		[InlineData("has space@example.com")]
		[InlineData("@example.com")]
		public void Addresses_that_are_not_addresses_are_rejected(string email)
		{
			Assert.Equal(SettingsFailure.EmailInvalid, TranslationSettingsValidator.Validate(email, "ru"));
		}

		[Theory]
		[InlineData("reader@example.com")]
		[InlineData("first.last+tag@sub.example.co.uk")]
		public void Ordinary_addresses_are_accepted(string email)
		{
			Assert.Equal(SettingsFailure.None, TranslationSettingsValidator.Validate(email, "ru"));
		}

		[Fact]
		public void An_absurdly_long_address_is_rejected()
		{
			string tooLong = new string('a', TranslationSettingsValidator.MaximumEmailLength) + "@example.com";

			Assert.Equal(SettingsFailure.EmailInvalid, TranslationSettingsValidator.Validate(tooLong, "ru"));
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("zz")]                  // not a language we offer
		[InlineData("russian")]             // a name, not a code
		public void Languages_outside_the_offered_list_are_rejected(string? language)
		{
			Assert.Equal(
				SettingsFailure.LanguageUnsupported,
				TranslationSettingsValidator.Validate("reader@example.com", language));
		}

		[Fact]
		public void The_language_the_page_offers_is_the_language_the_server_takes()
		{
			// The form is built from this same list, so every row in it must validate. This is
			// the test that fails if the two ever drift apart (D31).
			foreach (TranslationLanguage language in TranslationLanguages.All)
			{
				Assert.Equal(
					SettingsFailure.None,
					TranslationSettingsValidator.Validate("reader@example.com", language.Code));
			}
		}

		[Fact]
		public void Casing_and_padding_are_settled_before_storage()
		{
			TranslationSettings settings = TranslationSettingsValidator.Normalize("  reader@example.com  ", "RU");

			Assert.Equal("reader@example.com", settings.Email);
			Assert.Equal("ru", settings.TargetLanguage);
		}

		[Fact]
		public void A_language_typed_in_any_casing_is_still_that_language()
		{
			Assert.Equal(SettingsFailure.None, TranslationSettingsValidator.Validate("reader@example.com", "RU"));
			Assert.Equal("ru", TranslationLanguages.Normalize("Ru"));
		}
	}
}
