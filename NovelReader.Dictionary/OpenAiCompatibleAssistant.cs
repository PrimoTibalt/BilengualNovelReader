using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NovelReader.Domain.RealTimeReader.Assistant;
using NovelReader.Domain.RealTimeReader.Definitions;

namespace NovelReader.Dictionary
{
	/// <summary>
	/// The reader's own AI, reached through the OpenAI chat completions dialect that every
	/// service in <see cref="AssistantServices"/> speaks (D35). Each call is one short message
	/// pair: the paragraph and the selection in, a definition or a translation out.
	/// </summary>
	internal sealed class OpenAiCompatibleAssistant(
		IHttpClientFactory httpClientFactory,
		ILogger<OpenAiCompatibleAssistant> logger) : IAssistant
	{
		internal const string HttpClientName = "Assistant";

		/// <summary>
		/// A paragraph is context, not the question. Novels have the odd page-long one, and
		/// there is no reason to pay for all of it to explain one word.
		/// </summary>
		internal const int MaximumParagraphLength = 2000;

		/// <summary>The definition box pages through senses; more than this is a dictionary entry, not an answer.</summary>
		private const int MaximumSenses = 3;

		/// <summary>
		/// Asks a reasoning model to answer straight away. Measured on glm-5.3: with it, a
		/// translation comes back in fifteen tokens; without it, the model spends four hundred
		/// thinking and the answer is cut off before it starts.
		/// </summary>
		private const string ReasoningEffort = "none";

		/// <summary>
		/// Models that refused <see cref="ReasoningEffort"/> — older ones reject the field, some
		/// reject the value. Remembered so each such model costs one refused call per process,
		/// not one per word.
		/// </summary>
		private readonly ConcurrentDictionary<string, bool> _rejectsReasoningEffort = new();

		private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

		private const string DefinitionInstructions = """
			You are the dictionary of someone reading an English novel. They selected a word or
			phrase; explain it.
			Answer with JSON only, in exactly this shape:
			{"senses":[{"partOfSpeech":"verb","text":"...","example":"..."}]}
			The first sense is the meaning the selection has in the paragraph given, in one or two
			plain sentences. Add at most two more senses, and only if they are genuinely different
			common meanings. "example" is one short sentence using the selection, or null.
			""";

		private const string TranslationInstructions = """
			You translate from English into {0}. Someone reading a novel selected a word or
			phrase; translate it as it is meant in the paragraph given.
			Answer with the translation only — no quotes, notes, explanations or transliteration.
			""";

		public async Task<WordDefinition?> DefineAsync(
			AssistantSettings settings,
			string selection,
			string? paragraph,
			CancellationToken cancellationToken = default)
		{
			string? content = await CompleteAsync(settings, DefinitionInstructions, Question(selection, paragraph), cancellationToken);
			if (content is null)
			{
				return null;
			}

			List<DefinitionSense> senses = ReadSenses(content);
			if (senses.Count == 0)
			{
				return null;
			}

			return new WordDefinition
			{
				Term = selection,
				Senses = senses,
				SourceName = settings.Model
			};
		}

		public async Task<string?> TranslateAsync(
			AssistantSettings settings,
			string selection,
			string? paragraph,
			string targetLanguageName,
			CancellationToken cancellationToken = default)
		{
			string instructions = string.Format(TranslationInstructions, targetLanguageName);
			string? content = await CompleteAsync(settings, instructions, Question(selection, paragraph), cancellationToken);

			string? translated = content?.Trim().Trim('"', '“', '”', '«', '»').Trim();
			return string.IsNullOrEmpty(translated) ? null : translated;
		}

		public Task<string?> AskAsync(
			AssistantSettings settings,
			string instructions,
			string question,
			CancellationToken cancellationToken = default) =>
			CompleteAsync(settings, instructions, question, cancellationToken);

		private static string Question(string selection, string? paragraph)
		{
			string context = (paragraph ?? string.Empty).Trim();
			if (context.Length > MaximumParagraphLength)
			{
				context = context[..MaximumParagraphLength] + "…";
			}

			return context.Length == 0
				? $"Selected: {selection}"
				: $"Paragraph:\n{context}\n\nSelected: {selection}";
		}

		/// <summary>The assistant's reply text, or null for anything other than a usable answer.</summary>
		private async Task<string?> CompleteAsync(
			AssistantSettings settings,
			string instructions,
			string question,
			CancellationToken cancellationToken)
		{
			AssistantService? service = AssistantServices.Find(settings.ServiceId);
			if (service is null)
			{
				return null;
			}

			string modelKey = $"{service.Id}/{settings.Model}";
			bool withReasoningEffort = !_rejectsReasoningEffort.ContainsKey(modelKey);

			(HttpStatusCode status, string? body) = await SendAsync(service, settings, instructions, question, withReasoningEffort, cancellationToken);

			if (status == HttpStatusCode.BadRequest && withReasoningEffort)
			{
				_rejectsReasoningEffort.TryAdd(modelKey, true);
				(status, body) = await SendAsync(service, settings, instructions, question, false, cancellationToken);
			}

			string? content = status == HttpStatusCode.OK && body is not null ? ReadContent(body) : null;
			if (content is null)
			{
				// The reader is shown the dictionary's answer instead, so without this a wrong key
				// or a misspelt model would fail silently for ever. The body is the service's own
				// error message; it never contains the key.
				logger.LogWarning(
					"AI {Service}/{Model} gave no usable answer: {Status} {Body}",
					service.Id, settings.Model, (int)status, body is null ? null : body[..Math.Min(body.Length, 300)]);
			}

			return content;
		}

		private async Task<(HttpStatusCode Status, string? Body)> SendAsync(
			AssistantService service,
			AssistantSettings settings,
			string instructions,
			string question,
			bool withReasoningEffort,
			CancellationToken cancellationToken)
		{
			// Deliberately no temperature and no token limit: newer OpenAI models refuse both
			// under their old names, and the answers asked for are a sentence or two anyway.
			JsonObject request = new()
			{
				["model"] = settings.Model,
				["messages"] = new JsonArray
				{
					new JsonObject { ["role"] = "system", ["content"] = instructions },
					new JsonObject { ["role"] = "user", ["content"] = question }
				}
			};

			if (withReasoningEffort)
			{
				request["reasoning_effort"] = ReasoningEffort;
			}

			using HttpRequestMessage message = new(HttpMethod.Post, new Uri(new Uri(service.BaseAddress), "chat/completions"))
			{
				// A bare media type, no `; charset=utf-8`. DigitalOcean's gateway answers 403
				// "not authorized" to the charset parameter — measured: the same key and body pass
				// without it — and StringContent adds it unless handed the header itself.
				Content = new StringContent(request.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
			};
			message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

			try
			{
				HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
				using HttpResponseMessage response = await httpClient.SendAsync(message, cancellationToken);
				string body = await response.Content.ReadAsStringAsync(cancellationToken);
				return (response.StatusCode, body);
			}
			catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
			{
				return (HttpStatusCode.ServiceUnavailable, null);
			}
		}

		/// <summary>
		/// <c>choices[0].message.content</c>. A reply cut off by the length limit is refused:
		/// half a definition is worse than the dictionary's whole one.
		/// </summary>
		internal static string? ReadContent(string body)
		{
			try
			{
				using JsonDocument document = JsonDocument.Parse(body);

				if (!document.RootElement.TryGetProperty("choices", out JsonElement choices)
					|| choices.ValueKind != JsonValueKind.Array
					|| choices.GetArrayLength() == 0)
				{
					return null;
				}

				JsonElement choice = choices[0];
				if (choice.TryGetProperty("finish_reason", out JsonElement finish)
					&& finish.ValueKind == JsonValueKind.String
					&& finish.GetString() == "length")
				{
					return null;
				}

				if (!choice.TryGetProperty("message", out JsonElement message)
					|| !message.TryGetProperty("content", out JsonElement content)
					|| content.ValueKind != JsonValueKind.String)
				{
					return null;
				}

				string text = content.GetString()!.Trim();
				return text.Length == 0 ? null : text;
			}
			catch (JsonException)
			{
				return null;
			}
		}

		/// <summary>
		/// The senses out of the model's JSON. Models wrap JSON in a code fence or a sentence
		/// often enough that the outermost braces are what is parsed, not the whole reply.
		/// </summary>
		internal static List<DefinitionSense> ReadSenses(string content)
		{
			int start = content.IndexOf('{');
			int end = content.LastIndexOf('}');
			if (start < 0 || end <= start)
			{
				return [];
			}

			try
			{
				SensesReply? reply = JsonSerializer.Deserialize<SensesReply>(content[start..(end + 1)], ReadOptions);

				return [.. (reply?.Senses ?? [])
					.Where(sense => !string.IsNullOrWhiteSpace(sense.Text))
					.Take(MaximumSenses)
					.Select(sense => new DefinitionSense
					{
						PartOfSpeech = string.IsNullOrWhiteSpace(sense.PartOfSpeech) ? null : sense.PartOfSpeech.Trim(),
						Text = sense.Text!.Trim(),
						Example = string.IsNullOrWhiteSpace(sense.Example) ? null : sense.Example.Trim()
					})];
			}
			catch (JsonException)
			{
				return [];
			}
		}

		private sealed record SensesReply(List<SenseReply>? Senses);

		private sealed record SenseReply(string? PartOfSpeech, string? Text, string? Example);
	}
}
