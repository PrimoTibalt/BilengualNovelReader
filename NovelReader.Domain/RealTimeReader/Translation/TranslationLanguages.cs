namespace NovelReader.Domain.RealTimeReader.Translation
{
	/// <summary>One language the reader can translate into.</summary>
	public sealed record TranslationLanguage(string Code, string Name);

	/// <summary>
	/// The languages offered in the settings form, defined here rather than on the page so the
	/// list the reader picks from and the list the server validates against cannot drift apart.
	/// Codes are ISO 639-1, which is what MyMemory's language pairs take.
	/// </summary>
	public static class TranslationLanguages
	{
		public static IReadOnlyList<TranslationLanguage> All { get; } =
		[
			new("ar", "Arabic"),
			new("bg", "Bulgarian"),
			new("cs", "Czech"),
			new("da", "Danish"),
			new("de", "German"),
			new("el", "Greek"),
			new("es", "Spanish"),
			new("et", "Estonian"),
			new("fa", "Persian"),
			new("fi", "Finnish"),
			new("fr", "French"),
			new("he", "Hebrew"),
			new("hi", "Hindi"),
			new("hu", "Hungarian"),
			new("id", "Indonesian"),
			new("it", "Italian"),
			new("ja", "Japanese"),
			new("kk", "Kazakh"),
			new("ko", "Korean"),
			new("lt", "Lithuanian"),
			new("lv", "Latvian"),
			new("nl", "Dutch"),
			new("no", "Norwegian"),
			new("pl", "Polish"),
			new("pt", "Portuguese"),
			new("ro", "Romanian"),
			new("ru", "Russian"),
			new("sk", "Slovak"),
			new("sr", "Serbian"),
			new("sv", "Swedish"),
			new("th", "Thai"),
			new("tr", "Turkish"),
			new("uk", "Ukrainian"),
			new("vi", "Vietnamese"),
			new("zh", "Chinese")
		];

		/// <summary>The language novels are read in, and so the left half of every language pair.</summary>
		public const string SourceLanguage = "en";

		public static bool IsSupported(string? code) =>
			code is not null && All.Any(language => string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase));

		/// <summary>The stored spelling, so casing is settled in one place.</summary>
		public static string? Normalize(string? code) =>
			All.FirstOrDefault(language => string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase))?.Code;
	}
}
