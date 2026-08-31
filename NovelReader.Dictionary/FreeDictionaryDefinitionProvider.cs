using System.Text.Json;
using NovelReader.Domain.RealTimeReader.Definitions;

namespace NovelReader.Dictionary
{
	/// <summary>
	/// Fallback provider (D1). Cleaner plain-text output than Wiktionary, but community-run
	/// and measured at ~21 s with stretches of Cloudflare 522s, so it is never on the
	/// critical path and runs under a short timeout.
	/// </summary>
	internal class FreeDictionaryDefinitionProvider(IHttpClientFactory httpClientFactory)
		: IDefinitionProvider
	{
		internal const string HttpClientName = "FreeDictionary";

		private const int MaxSenses = 12;

		public string Name => "Free Dictionary";

		public async Task<WordDefinition?> LookUpAsync(string term, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrWhiteSpace(term))
			{
				return null;
			}

			HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);

			HttpResponseMessage response;
			try
			{
				response = await httpClient.GetAsync(Uri.EscapeDataString(term), cancellationToken);
			}
			catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
			{
				return null;
			}

			if (!response.IsSuccessStatusCode)
			{
				return null;
			}

			try
			{
				await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken);
				using JsonDocument document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
				return BuildDefinition(term, document);
			}
			catch (JsonException)
			{
				return null;
			}
		}

		private WordDefinition? BuildDefinition(string term, JsonDocument document)
		{
			if (document.RootElement.ValueKind != JsonValueKind.Array)
			{
				return null;
			}

			List<DefinitionSense> senses = [];
			foreach (JsonElement entry in document.RootElement.EnumerateArray())
			{
				if (!entry.TryGetProperty("meanings", out JsonElement meanings)
					|| meanings.ValueKind != JsonValueKind.Array)
				{
					continue;
				}

				foreach (JsonElement meaning in meanings.EnumerateArray())
				{
					string? partOfSpeech = meaning.TryGetProperty("partOfSpeech", out JsonElement partOfSpeechElement)
						? partOfSpeechElement.GetString()
						: null;

					if (!meaning.TryGetProperty("definitions", out JsonElement definitions)
						|| definitions.ValueKind != JsonValueKind.Array)
					{
						continue;
					}

					foreach (JsonElement definition in definitions.EnumerateArray())
					{
						if (senses.Count >= MaxSenses)
						{
							break;
						}

						if (!definition.TryGetProperty("definition", out JsonElement textElement))
						{
							continue;
						}

						string? text = textElement.GetString();
						if (string.IsNullOrWhiteSpace(text))
						{
							continue;
						}

						senses.Add(new DefinitionSense
						{
							PartOfSpeech = partOfSpeech,
							Text = text.Trim(),
							Example = definition.TryGetProperty("example", out JsonElement exampleElement)
								? exampleElement.GetString()
								: null
						});
					}
				}
			}

			if (senses.Count == 0)
			{
				return null;
			}

			return new WordDefinition
			{
				Term = term,
				Senses = senses,
				SourceName = Name,
				SourceUrl = null
			};
		}
	}
}
