using System.Text.RegularExpressions;

namespace NovelReader.Domain.RealTimeReader.Translation
{
	/// <summary>Why a settings save was refused, or <see cref="None"/> when it was not.</summary>
	public enum SettingsFailure
	{
		None,
		EmailInvalid,
		LanguageUnsupported
	}

	/// <summary>
	/// The rules for translation settings, kept beside the other domain rules so they can be
	/// tested without a web host — and so the page's own checks are a convenience rather than
	/// the only thing standing between a typo and the provider (D31).
	/// </summary>
	public static partial class TranslationSettingsValidator
	{
		/// <summary>
		/// Deliberately loose. This is not an identity check and nothing is sent to confirm it:
		/// the email exists so the provider has someone to contact, so the only mistakes worth
		/// catching are the ones that mean "I did not enter an address at all".
		/// </summary>
		[GeneratedRegex(@"^[^@\s]{1,64}@[^@\s.]+(\.[^@\s.]+)+$")]
		private static partial Regex EmailPattern { get; }

		public const int MaximumEmailLength = 254;

		public static SettingsFailure Validate(string? email, string? languageCode)
		{
			string trimmed = (email ?? string.Empty).Trim();

			if (trimmed.Length is 0 or > MaximumEmailLength || !EmailPattern.IsMatch(trimmed))
			{
				return SettingsFailure.EmailInvalid;
			}

			return TranslationLanguages.IsSupported(languageCode)
				? SettingsFailure.None
				: SettingsFailure.LanguageUnsupported;
		}

		/// <summary>The stored form: trimmed address, canonical language code.</summary>
		public static TranslationSettings Normalize(string email, string languageCode) =>
			new(email.Trim(), TranslationLanguages.Normalize(languageCode)!);
	}
}
