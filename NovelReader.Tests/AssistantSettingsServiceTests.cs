using NovelReader.Domain.RealTimeReader.Assistant;

namespace NovelReader.Tests
{
	public class AssistantSettingsServiceTests
	{
		private readonly InMemoryAssistantSettingsStore store = new();
		private readonly ReversingProtector protector = new();
		private AssistantSettingsService Service => new(store, protector);

		[Fact]
		public async Task The_key_is_stored_protected_and_read_back_plain()
		{
			var (saved, failure) = await Service.SaveAsync("anton", "digitalocean", "glm-5.3", "sk-do-1234567890abcd");

			Assert.Equal(AssistantSettingsFailure.None, failure);
			Assert.Equal("abcd", saved!.KeyHint);
			Assert.DoesNotContain("sk-do-1234567890abcd", store.Rows["anton"].ProtectedApiKey);

			AssistantSettings? read = await Service.GetAsync("anton");
			Assert.Equal(new AssistantSettings("digitalocean", "glm-5.3", "sk-do-1234567890abcd"), read);
		}

		[Fact]
		public async Task An_empty_key_keeps_the_stored_one_so_the_model_can_change_alone()
		{
			await Service.SaveAsync("anton", "digitalocean", "glm-5.3", "sk-do-1234567890abcd");

			var (_, failure) = await Service.SaveAsync("anton", "digitalocean", "glm-5.3-flash", "  ");

			Assert.Equal(AssistantSettingsFailure.None, failure);
			Assert.Equal(new AssistantSettings("digitalocean", "glm-5.3-flash", "sk-do-1234567890abcd"), await Service.GetAsync("anton"));
		}

		[Fact]
		public async Task An_empty_key_with_nothing_stored_is_refused()
		{
			var (_, failure) = await Service.SaveAsync("anton", "digitalocean", "glm-5.3", "");

			Assert.Equal(AssistantSettingsFailure.KeyInvalid, failure);
			Assert.Empty(store.Rows);
		}

		[Theory]
		[InlineData("custom", "glm-5.3", "sk-1234567890", AssistantSettingsFailure.ServiceUnknown)]
		[InlineData("openai", "", "sk-1234567890", AssistantSettingsFailure.ModelInvalid)]
		[InlineData("openai", "gpt 5", "sk-1234567890", AssistantSettingsFailure.ModelInvalid)]
		[InlineData("openai", "gpt-5", "short", AssistantSettingsFailure.KeyInvalid)]
		[InlineData("openai", "gpt-5", "sk-1234 567890", AssistantSettingsFailure.KeyInvalid)]
		public async Task Settings_that_cannot_work_are_refused(string service, string model, string key, AssistantSettingsFailure expected)
		{
			var (_, failure) = await Service.SaveAsync("anton", service, model, key);

			Assert.Equal(expected, failure);
		}

		[Fact]
		public async Task A_key_that_can_no_longer_be_read_counts_as_not_configured()
		{
			await Service.SaveAsync("anton", "openai", "gpt-5", "sk-1234567890");
			protector.Broken = true;

			Assert.Null(await Service.GetAsync("anton"));
			Assert.Null(await Service.DescribeAsync("anton"));
		}

		[Fact]
		public async Task Remove_forgets_everything()
		{
			await Service.SaveAsync("anton", "openai", "gpt-5", "sk-1234567890");

			await Service.RemoveAsync("anton");

			Assert.Null(await Service.GetAsync("anton"));
		}
	}
}
