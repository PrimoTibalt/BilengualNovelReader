using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NovelReader.Dictionary
{
	/// <summary>The option Jev picked and the probability it gave it.</summary>
	internal sealed record JevChoice(string Option, double Probability);

	/// <summary>
	/// TypeSafe's Jev (D46, D47): a decision model that answers one question about a piece of
	/// text with probabilities rather than prose — about 350 ms and $0.042 per million input
	/// tokens. The app's key, not a reader's. Every failure is logged and answered with null; the
	/// callers all have something to fall back on.
	/// </summary>
	internal sealed class JevClient(
		IHttpClientFactory httpClientFactory,
		ILogger<JevClient> logger)
	{
		internal const string HttpClientName = "Jev";

		private const string Model = "jev-latest";

		private const string QuestionName = "q";

		/// <summary>Which of <paramref name="options"/> (name → what it means) fits <paramref name="state"/>.</summary>
		public async Task<JevChoice?> ChooseAsync(
			string state,
			string instructions,
			IReadOnlyDictionary<string, string> options,
			CancellationToken cancellationToken = default)
		{
			JsonObject criteria = [];
			foreach ((string name, string meaning) in options)
			{
				criteria[name] = meaning;
			}

			string? body = await AskAsync(state, new JsonObject
			{
				["type"] = "choice",
				["instructions"] = instructions.Trim(),
				["criteria"] = criteria
			}, cancellationToken);

			return body is null ? null : ReadChoice(body);
		}

		/// <summary>The probability that the answer to a yes/no question about <paramref name="state"/> is yes.</summary>
		public async Task<double?> YesProbabilityAsync(
			string state,
			string instructions,
			string yesMeans,
			string noMeans,
			CancellationToken cancellationToken = default)
		{
			string? body = await AskAsync(state, new JsonObject
			{
				["type"] = "noul",
				["instructions"] = instructions.Trim(),
				["criteria"] = new JsonObject { ["true"] = yesMeans, ["false"] = noMeans }
			}, cancellationToken);

			return body is null ? null : ReadYesProbability(body);
		}

		private async Task<string?> AskAsync(string state, JsonObject question, CancellationToken cancellationToken)
		{
			JsonObject request = new()
			{
				["model"] = Model,
				["state"] = state,
				["questions"] = new JsonObject { [QuestionName] = question }
			};

			HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
			try
			{
				using HttpResponseMessage response = await httpClient.PostAsJsonAsync("v1/systemone", request, cancellationToken);
				if (!response.IsSuccessStatusCode)
				{
					// 401 is a bad key, 429/529 a busy service; the caller falls back either way.
					logger.LogWarning(
						"Jev answered {Status} (request {RequestId})",
						(int)response.StatusCode,
						response.Headers.TryGetValues("x-typesafe-request-id", out IEnumerable<string>? ids) ? ids.FirstOrDefault() : null);
					return null;
				}

				return await response.Content.ReadAsStringAsync(cancellationToken);
			}
			catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
			{
				logger.LogWarning("Jev could not be reached: {Message}", exception.Message);
				return null;
			}
		}

		/// <summary>
		/// The picked option and its probability. The probability is read from
		/// <c>probabilities</c>: Jev's own <c>confidence</c> runs well below it (0.30 for an
		/// option given 0.53), so it is only the fallback when no probabilities came back.
		/// </summary>
		internal static JevChoice? ReadChoice(string body)
		{
			if (ReadAnswer(body) is not { } answer
				|| !answer.TryGetProperty("choice", out JsonElement choice)
				|| choice.ValueKind != JsonValueKind.String
				|| choice.GetString() is not { Length: > 0 } option)
			{
				return null;
			}

			double? probability =
				answer.TryGetProperty("probabilities", out JsonElement probabilities)
					&& probabilities.ValueKind == JsonValueKind.Object
					&& probabilities.TryGetProperty(option, out JsonElement given)
					&& given.ValueKind == JsonValueKind.Number
					? given.GetDouble()
					: answer.TryGetProperty("confidence", out JsonElement stated) && stated.ValueKind == JsonValueKind.Number
						? stated.GetDouble()
						: null;

			return probability is null ? null : new JevChoice(option, probability.Value);
		}

		internal static double? ReadYesProbability(string body) =>
			ReadAnswer(body) is { } answer
				&& answer.TryGetProperty("noul", out JsonElement yes)
				&& yes.ValueKind == JsonValueKind.Number
					? yes.GetDouble()
					: null;

		private static JsonElement? ReadAnswer(string body)
		{
			try
			{
				using JsonDocument document = JsonDocument.Parse(body);
				return document.RootElement.ValueKind == JsonValueKind.Object
					&& document.RootElement.TryGetProperty("answers", out JsonElement answers)
					&& answers.ValueKind == JsonValueKind.Object
					&& answers.TryGetProperty(QuestionName, out JsonElement answer)
					&& answer.ValueKind == JsonValueKind.Object
						? answer.Clone()
						: null;
			}
			catch (JsonException)
			{
				return null;
			}
		}
	}
}
