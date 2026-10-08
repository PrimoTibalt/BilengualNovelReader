namespace NovelReader.Domain.RealTimeReader.Assistant
{
	/// <summary>
	/// Which AI the reader wants their definitions and translations from, and the key to pay
	/// for it with. <see cref="ApiKey"/> is the plain key: it is protected on the way into the
	/// store and never sent back to the page (D35).
	/// </summary>
	public sealed record AssistantSettings(string ServiceId, string Model, string ApiKey);

	/// <summary>
	/// What the page is told about the stored settings — enough to show them, never the key.
	/// <see cref="KeyHint"/> is the key's last four characters, so a reader with two keys can
	/// tell which one is in use.
	/// </summary>
	public sealed record AssistantSettingsSummary(string ServiceId, string Model, string KeyHint);

	/// <summary>The row as it is stored: the key in its protected form.</summary>
	public sealed record StoredAssistantSettings(string ServiceId, string Model, string ProtectedApiKey);
}
