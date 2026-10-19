using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NovelReader.Domain.RealTimeReader.Review;

namespace NovelReader.Dictionary
{
	/// <summary>
	/// The review's quick judge (D46): TypeSafe's Jev, a decision model that answers a
	/// multiple-choice question about a piece of text with probabilities rather than prose. One
	/// <c>choice</c> question per answer, its options the three verdicts. The app's key, not the
	/// reader's — it costs a few millionths of a dollar an answer.
	/// </summary>
	internal sealed class JevAnswerJudge(
		IHttpClientFactory httpClientFactory,
		ILogger<JevAnswerJudge> logger) : IAnswerJudge
	{
		internal const string HttpClientName = "Jev";

		private const string Model = "jev-latest";

		private const string QuestionName = "verdict";

		/// <summary>
		/// Below this, Jev is torn between verdicts, and the answer is better left to the reader's
		/// AI, which can also say what was wrong with it.
		/// </summary>
		internal const double MinimumConfidence = 0.6;

		private static readonly IReadOnlyDictionary<AnswerVerdict, string> OptionNames = new Dictionary<AnswerVerdict, string>
		{
			[AnswerVerdict.Correct] = "correct",
			[AnswerVerdict.Partial] = "partial",
			[AnswerVerdict.Wrong] = "wrong"
		};

		public async Task<AnswerVerdict?> JudgeAsync(
			string instructions,
			IReadOnlyDictionary<AnswerVerdict, string> verdicts,
			string answer,
			CancellationToken cancellationToken = default)
		{
			JsonObject criteria = [];
			foreach ((AnswerVerdict verdict, string meaning) in verdicts)
			{
				criteria[OptionNames[verdict]] = meaning;
			}

			JsonObject request = new()
			{
				["model"] = Model,
				["state"] = answer,
				["questions"] = new JsonObject
				{
					[QuestionName] = new JsonObject
					{
						["type"] = "choice",
						["instructions"] = instructions.Trim(),
						["criteria"] = criteria
					}
				}
			};

			HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);

			string body;
			try
			{
				using HttpResponseMessage response = await httpClient.PostAsJsonAsync("v1/systemone", request, cancellationToken);
				if (!response.IsSuccessStatusCode)
				{
					// 401 is a bad key, 429/529 a busy service; the reader's AI takes the answer either way.
					logger.LogWarning(
						"Jev answered {Status} (request {RequestId})",
						(int)response.StatusCode,
						response.Headers.TryGetValues("x-typesafe-request-id", out IEnumerable<string>? ids) ? ids.FirstOrDefault() : null);
					return null;
				}

				body = await response.Content.ReadAsStringAsync(cancellationToken);
			}
			catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
			{
				logger.LogWarning("Jev could not be reached: {Message}", exception.Message);
				return null;
			}

			return ReadVerdict(body);
		}

		/// <summary>
		/// The chosen verdict, or null when the reply is unreadable or Jev was not sure. The top
		/// option's probability is taken from <c>probabilities</c> when given, else <c>confidence</c>.
		/// </summary>
		internal static AnswerVerdict? ReadVerdict(string body)
		{
			try
			{
				using JsonDocument document = JsonDocument.Parse(body);
				if (!document.RootElement.TryGetProperty("answers", out JsonElement answers)
					|| !answers.TryGetProperty(QuestionName, out JsonElement answer)
					|| !answer.TryGetProperty("choice", out JsonElement choice)
					|| choice.ValueKind != JsonValueKind.String)
				{
					return null;
				}

				string chosen = choice.GetString()!;
				AnswerVerdict? verdict = OptionNames
					.Where(option => option.Value == chosen)
					.Select(option => (AnswerVerdict?)option.Key)
					.FirstOrDefault();
				if (verdict is null)
				{
					return null;
				}

				double? confidence =
					answer.TryGetProperty("probabilities", out JsonElement probabilities)
						&& probabilities.ValueKind == JsonValueKind.Object
						&& probabilities.TryGetProperty(chosen, out JsonElement probability)
						&& probability.ValueKind == JsonValueKind.Number
						? probability.GetDouble()
						: answer.TryGetProperty("confidence", out JsonElement stated) && stated.ValueKind == JsonValueKind.Number
							? stated.GetDouble()
							: null;

				return confidence >= MinimumConfidence ? verdict : null;
			}
			catch (JsonException)
			{
				return null;
			}
		}
	}
}
