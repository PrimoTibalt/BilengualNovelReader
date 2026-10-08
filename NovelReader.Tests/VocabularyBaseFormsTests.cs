using NovelReader.Domain.RealTimeReader.Definitions;
using NovelReader.Domain.RealTimeReader.Vocabulary;

namespace NovelReader.Tests
{
	/// <summary>Words saved before base forms existed get the dictionary's base (D39).</summary>
	public class VocabularyBaseFormsTests
	{
		private readonly InMemoryVocabularyRepository vocabulary = new();
		private readonly DeferredBackgroundWorkScheduler scheduler = new();

		private VocabularyBaseForms Service(params (string Term, string Sense)[] dictionary) => new(
			vocabulary,
			new DefinitionLookupService(new FixedDefinitionProvider(dictionary), new NoDefinitionCache()),
			new InMemoryPreparedChapterCache(),
			scheduler);

		[Fact]
		public async Task An_old_entry_gets_the_base_its_first_sense_names()
		{
			await vocabulary.AddAsync("anton", Entry("sneered"));

			int updated = await Service(("sneered", "simple past and past participle of sneer")).BackfillAsync("anton");

			Assert.Equal(1, updated);
			Assert.Equal("sneer", vocabulary.Single("anton", "sneered").BaseForm);
		}

		[Fact]
		public async Task An_old_entry_the_dictionary_defines_as_itself_becomes_its_own_base()
		{
			// Left to the rules, a saved `evening` would match every "even".
			await vocabulary.AddAsync("anton", Entry("evening"));

			await Service(("evening", "The time of the day between dusk and night.")).BackfillAsync("anton");

			Assert.Equal("evening", vocabulary.Single("anton", "evening").BaseForm);
		}

		[Fact]
		public async Task A_word_no_dictionary_knows_is_left_to_the_rules()
		{
			await vocabulary.AddAsync("anton", Entry("gu"));

			Assert.Equal(0, await Service().BackfillAsync("anton"));
			Assert.Null(vocabulary.Single("anton", "gu").BaseForm);
		}

		[Fact]
		public async Task It_is_scheduled_once_per_reader()
		{
			VocabularyBaseForms service = Service();

			service.EnsureFor("anton");
			service.EnsureFor("Anton");
			service.EnsureFor("someone-else");

			Assert.Equal(2, scheduler.Descriptions.Count);
			await Task.CompletedTask;
		}

		private static VocabularyEntry Entry(string term) => new()
		{
			NormalizedTerm = term,
			SurfaceForm = term,
			NovelName = "novel",
			SavedAtUtc = DateTime.UnixEpoch
		};

		private sealed class FixedDefinitionProvider((string Term, string Sense)[] entries) : IDefinitionProvider
		{
			public string Name => "fixed";

			public Task<WordDefinition?> LookUpAsync(string term, CancellationToken cancellationToken = default)
			{
				string[] senses = [.. entries.Where(entry => entry.Term == term).Select(entry => entry.Sense)];
				return Task.FromResult(senses.Length == 0
					? null
					: new WordDefinition
					{
						Term = term,
						Senses = [.. senses.Select(text => new DefinitionSense { Text = text })],
						SourceName = Name
					});
			}
		}

		private sealed class NoDefinitionCache : IDefinitionCache
		{
			public Task<DefinitionLookupResult> TryGetAsync(string normalizedTerm, CancellationToken cancellationToken = default) =>
				Task.FromResult(DefinitionLookupResult.Unknown);

			public Task StoreAsync(WordDefinition definition, CancellationToken cancellationToken = default) => Task.CompletedTask;

			public Task StoreMissAsync(string normalizedTerm, CancellationToken cancellationToken = default) => Task.CompletedTask;
		}
	}

	internal sealed class InMemoryVocabularyRepository : IVocabularyRepository
	{
		private readonly Dictionary<(string User, string Term), VocabularyEntry> entries = [];

		public VocabularyEntry Single(string userName, string term) => entries[(userName, term)];

		public Task AddAsync(string userName, VocabularyEntry entry, CancellationToken cancellationToken = default)
		{
			entries[(userName, entry.NormalizedTerm)] = entry;
			return Task.CompletedTask;
		}

		public Task RemoveAsync(string userName, string normalizedTerm, CancellationToken cancellationToken = default)
		{
			entries.Remove((userName, normalizedTerm));
			return Task.CompletedTask;
		}

		public Task<bool> ContainsAsync(string userName, string normalizedTerm, CancellationToken cancellationToken = default) =>
			Task.FromResult(entries.ContainsKey((userName, normalizedTerm)));

		public Task<IReadOnlyCollection<VocabularyEntry>> GetAllForUserAsync(string userName, CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyCollection<VocabularyEntry>>([.. entries.Where(pair => pair.Key.User == userName).Select(pair => pair.Value)]);
	}
}
