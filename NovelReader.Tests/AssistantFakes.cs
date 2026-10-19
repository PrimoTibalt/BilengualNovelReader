using System.Net;
using NovelReader.Domain.RealTimeReader.Assistant;
using NovelReader.Domain.RealTimeReader.Definitions;
using NovelReader.Domain.RealTimeReader.Translation;

namespace NovelReader.Tests
{
	internal sealed class InMemoryAssistantSettingsStore : IAssistantSettingsStore
	{
		public Dictionary<string, StoredAssistantSettings> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

		public Task<StoredAssistantSettings?> GetAsync(string userName, CancellationToken cancellationToken = default) =>
			Task.FromResult(Rows.GetValueOrDefault(userName));

		public Task SaveAsync(string userName, StoredAssistantSettings settings, CancellationToken cancellationToken = default)
		{
			Rows[userName] = settings;
			return Task.CompletedTask;
		}

		public Task RemoveAsync(string userName, CancellationToken cancellationToken = default)
		{
			Rows.Remove(userName);
			return Task.CompletedTask;
		}
	}

	/// <summary>Reversible and obviously not the plain text, which is all the tests need to see.</summary>
	internal sealed class ReversingProtector : ISecretProtector
	{
		public bool Broken { get; set; }

		public string Protect(string secret) => "protected:" + new string([.. secret.Reverse()]);

		public string? Unprotect(string protectedSecret) =>
			Broken || !protectedSecret.StartsWith("protected:")
				? null
				: new string([.. protectedSecret["protected:".Length..].Reverse()]);
	}

	/// <summary>Answers from canned values and records what it was asked.</summary>
	internal sealed class ScriptedAssistant : IAssistant
	{
		public WordDefinition? Definition { get; set; }
		public string? Translation { get; set; }
		public string? LastParagraph { get; private set; }
		public string? LastLanguage { get; private set; }
		public int Calls { get; private set; }

		public Task<WordDefinition?> DefineAsync(AssistantSettings settings, string selection, string? paragraph, CancellationToken cancellationToken = default)
		{
			Calls++;
			LastParagraph = paragraph;
			return Task.FromResult(Definition);
		}

		public Task<string?> TranslateAsync(AssistantSettings settings, string selection, string? paragraph, string targetLanguageName, CancellationToken cancellationToken = default)
		{
			Calls++;
			LastParagraph = paragraph;
			LastLanguage = targetLanguageName;
			return Task.FromResult(Translation);
		}

		/// <summary>Answers by the first rule whose key appears in the instructions.</summary>
		public Dictionary<string, Func<string, string?>> Answers { get; } = [];
		public List<(string Instructions, string Question)> Asked { get; } = [];

		public Task<string?> AskAsync(AssistantSettings settings, string instructions, string question, CancellationToken cancellationToken = default)
		{
			Calls++;
			Asked.Add((instructions, question));
			Func<string, string?>? answer = Answers.FirstOrDefault(rule => instructions.Contains(rule.Key)).Value;
			return Task.FromResult(answer?.Invoke(question));
		}
	}

	internal sealed class InMemoryTranslationSettingsStore : ITranslationSettingsStore
	{
		public TranslationSettings? Settings { get; set; }

		public Task<TranslationSettings?> GetAsync(string userName, CancellationToken cancellationToken = default) =>
			Task.FromResult(Settings);

		public Task SaveAsync(string userName, TranslationSettings settings, CancellationToken cancellationToken = default)
		{
			Settings = settings;
			return Task.CompletedTask;
		}
	}

	internal sealed class FixedTranslationProvider(string? answer) : ITranslationProvider
	{
		public int Calls { get; private set; }

		public string Name => "MyMemory";

		public Task<Translation?> TranslateAsync(string text, string targetLanguage, string contactEmail, CancellationToken cancellationToken = default)
		{
			Calls++;
			return Task.FromResult(answer is null ? null : new Translation(answer, targetLanguage, Name));
		}
	}

	/// <summary>Answers each request with the next response in line, and keeps every request body.</summary>
	internal sealed class SequencedHttpMessageHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
	{
		private int _next;

		public List<string> RequestBodies { get; } = [];
		public List<HttpRequestMessage> Requests { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(request);
			RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));

			(HttpStatusCode status, string body) = responses[Math.Min(_next++, responses.Length - 1)];
			return new HttpResponseMessage(status) { Content = new StringContent(body) };
		}
	}
}
