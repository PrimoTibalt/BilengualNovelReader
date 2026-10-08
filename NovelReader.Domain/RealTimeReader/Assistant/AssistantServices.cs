namespace NovelReader.Domain.RealTimeReader.Assistant
{
	/// <summary>
	/// One AI service a reader can bring a key for. Every one of them speaks the OpenAI chat
	/// completions dialect at <see cref="BaseAddress"/>, so one client serves them all.
	/// </summary>
	public sealed record AssistantService(string Id, string Name, string BaseAddress);

	/// <summary>
	/// The services on offer, fixed here rather than typed in by the reader (D35). A base URL
	/// the reader could set is a URL the server could be told to call — any address on its own
	/// network included — with the reader's key in the header. Picking from a list closes that.
	/// </summary>
	public static class AssistantServices
	{
		public static IReadOnlyList<AssistantService> All { get; } =
		[
			new("digitalocean", "DigitalOcean", "https://inference.do-ai.run/v1/"),
			new("openai", "OpenAI", "https://api.openai.com/v1/"),
			new("anthropic", "Anthropic", "https://api.anthropic.com/v1/"),
			new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1/"),
			new("deepseek", "DeepSeek", "https://api.deepseek.com/v1/"),
			new("mistral", "Mistral", "https://api.mistral.ai/v1/"),
			new("groq", "Groq", "https://api.groq.com/openai/v1/")
		];

		public static AssistantService? Find(string? id) =>
			All.FirstOrDefault(service => string.Equals(service.Id, id, StringComparison.OrdinalIgnoreCase));
	}
}
