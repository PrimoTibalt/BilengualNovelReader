using System.Net;
using System.Text.Json;
using NovelReader.Domain.RealTimeReader.Definitions;

namespace NovelReader.Dictionary
{
	/// <summary>
	/// Primary provider (D1). Wikimedia-hosted, sub-second in measurement, and returns a
	/// clean 404 for a word it does not have.
	/// </summary>
	internal class WiktionaryDefinitionProvider(IHttpClientFactory httpClientFactory)
		: IDefinitionProvider
	{
		internal const string HttpClientName = "Wiktionary";

		/// <summary>Enough senses to be useful without turning the box into a wall of text.</summary>
		private const int MaxSenses = 12;

		public string Name => "Wiktionary";

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
				// A provider that is down or slow must not surface as an error to the reader;
				// the caller falls through to the next provider.
				return null;
			}

			if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
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
			if (document.RootElement.ValueKind != JsonValueKind.Object
				|| !document.RootElement.TryGetProperty("en", out JsonElement englishUsages)
				|| englishUsages.ValueKind != JsonValueKind.Array)
			{
				return null;
			}

			List<DefinitionSense> senses = [];
			foreach (JsonElement usage in englishUsages.EnumerateArray())
			{
				string? partOfSpeech = usage.TryGetProperty("partOfSpeech", out JsonElement partOfSpeechElement)
					? partOfSpeechElement.GetString()
					: null;

				if (!usage.TryGetProperty("definitions", out JsonElement definitions)
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

					string text = HtmlFragmentStripper.ToPlainText(textElement.GetString());
					if (text.Length == 0)
					{
						continue;
					}

					senses.Add(new DefinitionSense
					{
						PartOfSpeech = partOfSpeech,
						Text = text,
						Example = ReadFirstExample(definition)
					});
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
				SourceUrl = $"https://en.wiktionary.org/wiki/{Uri.EscapeDataString(term)}"
			};
		}

		private static string? ReadFirstExample(JsonElement definition)
		{
			if (!definition.TryGetProperty("examples", out JsonElement examples)
				|| examples.ValueKind != JsonValueKind.Array)
			{
				return null;
			}

			foreach (JsonElement example in examples.EnumerateArray())
			{
				string plainText = HtmlFragmentStripper.ToPlainText(example.GetString());
				if (plainText.Length > 0)
				{
					return plainText;
				}
			}

			return null;
		}
	}
}
