using System.Text.Json;
using NovelReader.Domain.RealTimeReader.Translation;

namespace NovelReader.Dictionary
{
	/// <summary>
	/// Translation through MyMemory's public API (D31). No key and no account: a request that
	/// carries the reader's own email claims their allowance rather than a shared one.
	/// </summary>
	internal sealed class MyMemoryTranslationProvider(IHttpClientFactory httpClientFactory)
		: ITranslationProvider
	{
		internal const string HttpClientName = "MyMemory";

		/// <summary>
		/// The API takes at most 500 bytes of query text. Nothing the reader can select comes
		/// close — the page caps a selection at four words — but a request that would be
		/// rejected is not worth sending.
		/// </summary>
		private const int MaximumTextBytes = 450;

		/// <summary>
		/// Out of allowance, MyMemory answers 200 and puts a shouted warning where the
		/// translation goes. Rendering that to the reader as if it were the answer is exactly
		/// the junk-chapter mistake D21 was written about, so it is recognised and refused.
		/// </summary>
		private const string QuotaWarningMarker = "MYMEMORY WARNING";

		public string Name => "MyMemory";

		public async Task<Translation?> TranslateAsync(
			string text,
			string targetLanguage,
			string contactEmail,
			CancellationToken cancellationToken = default)
		{
			string trimmed = (text ?? string.Empty).Trim();
			if (trimmed.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(trimmed) > MaximumTextBytes)
			{
				return null;
			}

			string query =
				$"get?q={Uri.EscapeDataString(trimmed)}" +
				$"&langpair={TranslationLanguages.SourceLanguage}|{Uri.EscapeDataString(targetLanguage)}" +
				$"&de={Uri.EscapeDataString(contactEmail)}";

			HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);

			string body;
			try
			{
				HttpResponseMessage response = await httpClient.GetAsync(query, cancellationToken);
				if (!response.IsSuccessStatusCode)
				{
					return null;
				}

				body = await response.Content.ReadAsStringAsync(cancellationToken);
			}
			catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
			{
				// Down or slow is not the reader's problem; the caller reports it as a failed
				// translation and the reading session carries on.
				return null;
			}

			string? translated = ReadTranslatedText(body);
			if (translated is null || translated.Contains(QuotaWarningMarker, StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			return new Translation(translated, targetLanguage, Name);
		}

		/// <summary>
		/// Pulls the translation out, refusing anything the API flagged. <c>responseStatus</c>
		/// arrives as a number on a good day and as a string on others, so both are accepted.
		/// </summary>
		private static string? ReadTranslatedText(string body)
		{
			try
			{
				using JsonDocument document = JsonDocument.Parse(body);

				if (document.RootElement.TryGetProperty("responseStatus", out JsonElement status))
				{
					int code = status.ValueKind switch
					{
						JsonValueKind.Number => status.GetInt32(),
						JsonValueKind.String => int.TryParse(status.GetString(), out int parsed) ? parsed : 0,
						_ => 0
					};

					if (code != 200)
					{
						return null;
					}
				}

				if (!document.RootElement.TryGetProperty("responseData", out JsonElement data)
					|| !data.TryGetProperty("translatedText", out JsonElement translated)
					|| translated.ValueKind != JsonValueKind.String)
				{
					return null;
				}

				string value = translated.GetString() ?? string.Empty;
				return value.Trim().Length == 0 ? null : value.Trim();
			}
			catch (JsonException)
			{
				return null;
			}
		}
	}
}
