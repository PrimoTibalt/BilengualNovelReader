namespace NovelReader.Domain.RealTimeReader.Assistant
{
	/// <summary>Why a save was refused, or <see cref="None"/> when it was not.</summary>
	public enum AssistantSettingsFailure
	{
		None,
		ServiceUnknown,
		ModelInvalid,
		KeyInvalid
	}

	/// <summary>
	/// The reader's AI settings: the rules for them, and the rule that the key is protected
	/// before it is stored and read back only here (D35).
	/// </summary>
	public sealed class AssistantSettingsService(IAssistantSettingsStore store, ISecretProtector protector)
	{
		public const int MaximumModelLength = 100;
		public const int MinimumKeyLength = 8;
		public const int MaximumKeyLength = 500;

		/// <summary>Null when nothing is stored, or the stored key can no longer be read.</summary>
		public async Task<AssistantSettings?> GetAsync(string userName, CancellationToken cancellationToken = default)
		{
			StoredAssistantSettings? stored = await store.GetAsync(userName, cancellationToken);
			if (stored is null || AssistantServices.Find(stored.ServiceId) is null)
			{
				return null;
			}

			string? key = protector.Unprotect(stored.ProtectedApiKey);
			return key is null ? null : new AssistantSettings(stored.ServiceId, stored.Model, key);
		}

		public async Task<AssistantSettingsSummary?> DescribeAsync(string userName, CancellationToken cancellationToken = default)
		{
			AssistantSettings? settings = await GetAsync(userName, cancellationToken);
			return settings is null ? null : Summarise(settings);
		}

		/// <summary>
		/// Stores the settings. An empty <paramref name="apiKey"/> keeps the key already stored,
		/// so a reader can change model without pasting the key in again.
		/// </summary>
		public async Task<(AssistantSettingsSummary? Saved, AssistantSettingsFailure Failure)> SaveAsync(
			string userName,
			string? serviceId,
			string? model,
			string? apiKey,
			CancellationToken cancellationToken = default)
		{
			AssistantService? service = AssistantServices.Find(serviceId);
			if (service is null)
			{
				return (null, AssistantSettingsFailure.ServiceUnknown);
			}

			string trimmedModel = (model ?? string.Empty).Trim();
			if (trimmedModel.Length is 0 or > MaximumModelLength || trimmedModel.Any(char.IsWhiteSpace))
			{
				return (null, AssistantSettingsFailure.ModelInvalid);
			}

			string trimmedKey = (apiKey ?? string.Empty).Trim();
			if (trimmedKey.Length == 0)
			{
				AssistantSettings? existing = await GetAsync(userName, cancellationToken);
				if (existing is null)
				{
					return (null, AssistantSettingsFailure.KeyInvalid);
				}

				trimmedKey = existing.ApiKey;
			}

			// A key is one token; whitespace inside it means something else was pasted with it.
			if (trimmedKey.Length is < MinimumKeyLength or > MaximumKeyLength || trimmedKey.Any(char.IsWhiteSpace))
			{
				return (null, AssistantSettingsFailure.KeyInvalid);
			}

			AssistantSettings settings = new(service.Id, trimmedModel, trimmedKey);
			await store.SaveAsync(
				userName,
				new StoredAssistantSettings(settings.ServiceId, settings.Model, protector.Protect(settings.ApiKey)),
				cancellationToken);

			return (Summarise(settings), AssistantSettingsFailure.None);
		}

		public Task RemoveAsync(string userName, CancellationToken cancellationToken = default) =>
			store.RemoveAsync(userName, cancellationToken);

		private static AssistantSettingsSummary Summarise(AssistantSettings settings) =>
			new(settings.ServiceId, settings.Model, settings.ApiKey[^4..]);
	}
}
