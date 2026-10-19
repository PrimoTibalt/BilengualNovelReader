using System.Text.Json;
using NovelReader.Domain.RealTimeReader.Assistant;

namespace NovelReader.Domain.RealTimeReader.Review
{
	/// <summary>A sentence for the reader to translate, and an English rendering that uses the word.</summary>
	public sealed record TranslationTask(string Sentence, string English);

	/// <summary>The AI's judgement of a free answer.</summary>
	public sealed record Judgement(AnswerVerdict Verdict, string Feedback);

	/// <summary>
	/// The review's use of the reader's AI: it writes the one-line definitions a match needs, the
	/// sentences a translation exercise starts from, and judges what the reader wrote or said.
	/// Every answer is asked for as JSON and read defensively; anything unreadable is null, and
	/// the caller grades nothing rather than guess.
	/// </summary>
	public sealed class ReviewTutor(IAssistant assistant)
	{
		/// <summary>Longest answer the reader may send, which is far more than any sentence.</summary>
		public const int MaximumAnswerLength = 1000;

		private const string GlossInstructions = """
			You write very short dictionary definitions for someone learning English vocabulary.
			For each word or phrase given, write its most common meaning in at most 12 words.
			Do not use the word itself or any form of it in its definition.
			Answer with JSON only, in exactly this shape: {"glosses":{"word":"definition"}}
			""";

		private const string RecallInstructions = """
			You check an English learner's vocabulary. They were asked what a word means and
			answered in their own words, perhaps by voice, so ignore spelling, punctuation and
			transcription slips. Judge only whether they know the meaning: "correct" if they do,
			"partial" if they are close or vague, "wrong" otherwise. A different real meaning of the
			word counts as correct.
			Answer with JSON only: {"verdict":"correct|partial|wrong","feedback":"..."}
			The feedback is one or two short sentences to the learner: confirm, or say what was
			missing or wrong and give the meaning.
			""";

		private const string SentenceInstructions = """
			You check an English learner's vocabulary. They were asked to write a sentence using a
			word, perhaps by voice, so ignore spelling, punctuation and transcription slips.
			"correct": the word, or a form of it, is used with a meaning it really has and fits
			the sentence naturally. "partial": the meaning comes through but the word is used
			awkwardly. "wrong": the word is missing, or used with a meaning it does not have.
			Answer with JSON only: {"verdict":"correct|partial|wrong","feedback":"..."}
			The feedback is one or two short sentences to the learner; when the usage is off, show
			a natural way to use the word.
			""";

		private const string TranslationTaskInstructions = """
			You set translation exercises for someone learning English whose own language is {0}.
			Write one natural sentence in {0}, 6 to 15 words long, whose most natural English
			translation uses the English word or phrase given, in the meaning given. Do not include
			the English word in the {0} sentence.
			Answer with JSON only: {{"sentence":"... in {0}","english":"... the English translation"}}
			""";

		private const string TranslationJudgeInstructions = """
			You check an English learner's translation from {0} into English, made perhaps by
			voice, so ignore spelling, punctuation and transcription slips. They were meant to use
			a given English word. "correct": a faithful translation that uses the word or a form of
			it. "partial": the meaning is right but the word is not used, or the word is used but
			the translation is noticeably off. "wrong": otherwise.
			Answer with JSON only: {{"verdict":"correct|partial|wrong","feedback":"..."}}
			The feedback is one or two short sentences to the learner, in English.
			""";

		/// <summary>One-line definitions for <paramref name="words"/>, keyed by the word; missing ones are left out.</summary>
		public async Task<IReadOnlyDictionary<string, string>> GlossesAsync(
			AssistantSettings settings,
			IReadOnlyCollection<string> words,
			CancellationToken cancellationToken = default)
		{
			if (words.Count == 0)
			{
				return new Dictionary<string, string>();
			}

			string? content = await assistant.AskAsync(settings, GlossInstructions, string.Join('\n', words), cancellationToken);
			Dictionary<string, string> glosses = new(StringComparer.OrdinalIgnoreCase);
			if (ReadObject(content) is not { } root
				|| !root.TryGetProperty("glosses", out JsonElement map)
				|| map.ValueKind != JsonValueKind.Object)
			{
				return glosses;
			}

			foreach (JsonProperty property in map.EnumerateObject())
			{
				if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } gloss)
				{
					glosses[property.Name.Trim()] = gloss.Trim();
				}
			}

			return glosses;
		}

		public async Task<Judgement?> JudgeRecallAsync(
			AssistantSettings settings,
			string word,
			string reference,
			string answer,
			CancellationToken cancellationToken = default)
		{
			string question = $"Word: {word}\nIts meaning: {reference}\nLearner's answer: {answer}";
			return ReadJudgement(await assistant.AskAsync(settings, RecallInstructions, question, cancellationToken));
		}

		public async Task<Judgement?> JudgeSentenceAsync(
			AssistantSettings settings,
			string word,
			string reference,
			string answer,
			CancellationToken cancellationToken = default)
		{
			string question = $"Word: {word}\nIts meaning: {reference}\nLearner's sentence: {answer}";
			return ReadJudgement(await assistant.AskAsync(settings, SentenceInstructions, question, cancellationToken));
		}

		public async Task<TranslationTask?> TranslationTaskAsync(
			AssistantSettings settings,
			string word,
			string reference,
			string languageName,
			CancellationToken cancellationToken = default)
		{
			string instructions = string.Format(TranslationTaskInstructions, languageName);
			string question = $"English word: {word}\nMeaning: {reference}";
			if (ReadObject(await assistant.AskAsync(settings, instructions, question, cancellationToken)) is not { } root)
			{
				return null;
			}

			string? sentence = ReadString(root, "sentence");
			string? english = ReadString(root, "english");
			return sentence is null || english is null ? null : new TranslationTask(sentence, english);
		}

		public async Task<Judgement?> JudgeTranslationAsync(
			AssistantSettings settings,
			string word,
			string languageName,
			TranslationTask task,
			string answer,
			CancellationToken cancellationToken = default)
		{
			string instructions = string.Format(TranslationJudgeInstructions, languageName);
			string question = $"Sentence in {languageName}: {task.Sentence}\nA good translation: {task.English}\n"
				+ $"Word to use: {word}\nLearner's translation: {answer}";
			return ReadJudgement(await assistant.AskAsync(settings, instructions, question, cancellationToken));
		}

		public static Judgement? ReadJudgement(string? content)
		{
			if (ReadObject(content) is not { } root)
			{
				return null;
			}

			AnswerVerdict? verdict = ReadString(root, "verdict")?.ToLowerInvariant() switch
			{
				"correct" => AnswerVerdict.Correct,
				"partial" => AnswerVerdict.Partial,
				"wrong" => AnswerVerdict.Wrong,
				_ => null
			};

			return verdict is null ? null : new Judgement(verdict.Value, ReadString(root, "feedback") ?? string.Empty);
		}

		/// <summary>
		/// The JSON object in a reply. Models wrap JSON in a code fence or a sentence often enough
		/// that the outermost braces are what is parsed, not the whole reply.
		/// </summary>
		public static JsonElement? ReadObject(string? content)
		{
			if (content is null)
			{
				return null;
			}

			int start = content.IndexOf('{');
			int end = content.LastIndexOf('}');
			if (start < 0 || end <= start)
			{
				return null;
			}

			try
			{
				using JsonDocument document = JsonDocument.Parse(content[start..(end + 1)]);
				return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
			}
			catch (JsonException)
			{
				return null;
			}
		}

		private static string? ReadString(JsonElement root, string name) =>
			root.TryGetProperty(name, out JsonElement value)
				&& value.ValueKind == JsonValueKind.String
				&& value.GetString()?.Trim() is { Length: > 0 } text
					? text
					: null;
	}
}
