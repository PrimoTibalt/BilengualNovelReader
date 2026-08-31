using NovelReader.Domain;
using NovelReader.Domain.RealTimeReader.Parsing;
using NovelReader.Domain.RealTimeReader.Reading;
using NovelReader.Domain.RealTimeReader.Vocabulary;

namespace NovelReader.Tests
{
	/// <summary>In-memory stand-ins for the reading pipeline's collaborators.</summary>
	internal sealed class FakeChapter : IChapter
	{
		public int ChapterNumber { get; init; }
		public Dictionary<string, object> Paragraphs { get; init; } = [];

		public bool TryGetParagraph(int paragraphNumber, out string paragraph)
		{
			if (Paragraphs.TryGetValue($"{paragraphNumber}", out object? found) && found is string text)
			{
				paragraph = text;
				return true;
			}

			paragraph = string.Empty;
			return false;
		}
	}

	internal sealed class FakeFilteredCollection(IChapter? chapter) : IFilteredCollection
	{
		public Task<IChapter?> TryGetExactlyOne() => Task.FromResult(chapter);
	}

	internal sealed class FakeCollectionOfChapters : ICollectionOfChapters
	{
		private readonly Lock gate = new();
		private readonly Dictionary<int, IChapter> stored = [];

		public Task<IFilteredCollection> FilterByChapter(int chapterNumber)
		{
			lock (gate)
			{
				stored.TryGetValue(chapterNumber, out IChapter? chapter);
				return Task.FromResult<IFilteredCollection>(new FakeFilteredCollection(chapter));
			}
		}

		public async Task<IChapter> InsertOneAsync(int chapterNumber, Lazy<Task<Dictionary<int, string>>> paragraphsLazy)
		{
			Dictionary<int, string> paragraphs = await paragraphsLazy.Value;
			FakeChapter chapter = new()
			{
				ChapterNumber = chapterNumber,
				Paragraphs = paragraphs.ToDictionary(pair => $"{pair.Key}", pair => (object)pair.Value)
			};

			lock (gate)
			{
				stored[chapterNumber] = chapter;
			}

			return chapter;
		}
	}

	internal sealed class FakeNovelRepository(FakeCollectionOfChapters chapters) : INovelRepository
	{
		public Task<ICollectionOfChapters> GetCollectionOfChapters(string novelName)
			=> Task.FromResult<ICollectionOfChapters>(chapters);
	}

	/// <summary>Counts scrapes per chapter, which is what the storm was made of.</summary>
	internal sealed class CountingParagraphsRetriever : IParagraphsRetriever
	{
		private readonly Lock gate = new();
		private readonly Dictionary<string, int> callsByPath = [];

		public int TotalCalls
		{
			get { lock (gate) { return callsByPath.Values.Sum(); } }
		}

		public int CallsFor(string uriPath)
		{
			lock (gate) { return callsByPath.GetValueOrDefault(uriPath); }
		}

		/// <summary>
		/// Scrapes of one chapter, matched on the chapter suffix rather than the whole path.
		/// Whether the path is written "book/…" or "/book/…" is the caller's business and has
		/// changed before; how many times a chapter was fetched is what these tests are about.
		/// </summary>
		public int CallsForChapter(int chapterNumber)
		{
			lock (gate)
			{
				int total = 0;
				foreach ((string path, int calls) in callsByPath)
				{
					if (path.EndsWith($"chapter-{chapterNumber}", StringComparison.Ordinal))
					{
						total += calls;
					}
				}

				return total;
			}
		}

		public Task<Dictionary<int, string>> GetParagraphsAsync(string uriPath)
		{
			lock (gate)
			{
				callsByPath[uriPath] = callsByPath.GetValueOrDefault(uriPath) + 1;
			}

			Dictionary<int, string> paragraphs = [];
			for (int number = 1; number <= 10; number++)
			{
				paragraphs[number] = $"paragraph {number} of {uriPath}";
			}

			return Task.FromResult(paragraphs);
		}
	}

	internal sealed class EmptyVocabularyRepository : IVocabularyRepository
	{
		public Task AddAsync(string userName, VocabularyEntry entry, CancellationToken cancellationToken = default)
			=> Task.CompletedTask;

		public Task RemoveAsync(string userName, string normalizedTerm, CancellationToken cancellationToken = default)
			=> Task.CompletedTask;

		public Task<bool> ContainsAsync(string userName, string normalizedTerm, CancellationToken cancellationToken = default)
			=> Task.FromResult(false);

		public Task<IReadOnlyCollection<VocabularyEntry>> GetAllForUserAsync(string userName, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyCollection<VocabularyEntry>>([]);
	}

	internal sealed class InMemoryPreparedChapterCache : IPreparedChapterCache
	{
		private readonly Lock gate = new();
		private readonly Dictionary<string, PreparedChapter> memory = [];
		private readonly Dictionary<string, PreparedChapter> durable = [];

		private static string Key(string userName, string novelName, int chapterNumber)
			=> $"{userName}/{novelName}/{chapterNumber}";

		public Task<PreparedChapter?> TryGetFromMemoryAsync(string userName, string novelName, int chapterNumber, CancellationToken cancellationToken = default)
		{
			lock (gate)
			{
				memory.TryGetValue(Key(userName, novelName, chapterNumber), out PreparedChapter? chapter);
				return Task.FromResult(chapter);
			}
		}

		public Task<PreparedChapter?> TryGetFromDurableAsync(string userName, string novelName, int chapterNumber, CancellationToken cancellationToken = default)
		{
			lock (gate)
			{
				durable.TryGetValue(Key(userName, novelName, chapterNumber), out PreparedChapter? chapter);
				return Task.FromResult(chapter);
			}
		}

		public Task StoreInDurableAsync(string userName, PreparedChapter chapter, CancellationToken cancellationToken = default)
		{
			lock (gate)
			{
				durable[Key(userName, chapter.NovelName, chapter.ChapterNumber)] = chapter;
			}

			return Task.CompletedTask;
		}

		public Task PromoteToMemoryAsync(string userName, PreparedChapter chapter, CancellationToken cancellationToken = default)
		{
			lock (gate)
			{
				string key = Key(userName, chapter.NovelName, chapter.ChapterNumber);
				memory[key] = chapter;
				durable.Remove(key);
			}

			return Task.CompletedTask;
		}

		public Task InvalidateForUserAsync(string userName, CancellationToken cancellationToken = default)
		{
			lock (gate)
			{
				memory.Clear();
				durable.Clear();
			}

			return Task.CompletedTask;
		}
	}

	/// <summary>
	/// Holds queued work instead of running it, so a test can see what was scheduled while
	/// earlier prefetches were still in flight — which is exactly when the storm happened.
	/// </summary>
	internal sealed class DeferredBackgroundWorkScheduler : IBackgroundWorkScheduler
	{
		private readonly Lock gate = new();
		private readonly List<Func<CancellationToken, Task>> queued = [];

		public List<string> Descriptions { get; } = [];

		public int ScheduledCount
		{
			get { lock (gate) { return queued.Count; } }
		}

		public void Schedule(Func<CancellationToken, Task> work, string description)
		{
			lock (gate)
			{
				queued.Add(work);
				Descriptions.Add(description);
			}
		}

		/// <summary>Runs everything queued so far, concurrently, and clears the queue.</summary>
		public async Task RunQueuedAsync()
		{
			Func<CancellationToken, Task>[] toRun;
			lock (gate)
			{
				toRun = [.. queued];
				queued.Clear();
			}

			await Task.WhenAll(toRun.Select(work => work(CancellationToken.None)));
		}
	}
}

namespace NovelReader.Tests
{
	using NovelReader.Domain.RealTimeReader.User;

	/// <summary>Progress storage without Mongo, recording what was written to it.</summary>
	internal sealed class InMemoryReadingProgressStore : IReadingProgressStore
	{
		private readonly Dictionary<string, NovelSummary> summaries = [];

		/// <summary>Every SaveNovelDetailsAsync call, in order.</summary>
		public List<(string Novel, NovelSummary? Summary, DateTime CheckedAt)> DetailWrites { get; } = [];

		public List<NovelSummary> Library { get; } = [];

		public Task<IReadOnlyList<NovelSummary>> GetNovelsReadAsync(string userName, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<NovelSummary>>(Library);

		public Task SaveNovelDetailsAsync(
			string userName,
			string novelName,
			NovelSummary? summary,
			DateTime checkedAtUtc,
			CancellationToken cancellationToken = default)
		{
			lock (DetailWrites)
			{
				DetailWrites.Add((novelName, summary, checkedAtUtc));
				summaries[novelName] = summary ?? new NovelSummary { Slug = novelName, CheckedAtUtc = checkedAtUtc };
			}

			return Task.CompletedTask;
		}

		public Task SaveAsync(string userName, ReadingProgress progress, CancellationToken cancellationToken = default)
			=> Task.CompletedTask;

		public Task<ReadingProgress?> GetAsync(string userName, string novelName, CancellationToken cancellationToken = default)
			=> Task.FromResult<ReadingProgress?>(null);

		public Task<ReadingProgress?> GetMostRecentAsync(string userName, CancellationToken cancellationToken = default)
			=> Task.FromResult<ReadingProgress?>(null);
	}

	/// <summary>A catalogue that is down.</summary>
	internal sealed class FailingSearchNovelsRetriever
		: NovelReader.Domain.RealTimeReader.Parsing.ISearchNovelsRetriever
	{
		public Task<IReadOnlyCollection<NovelDataDto>> GetNovelsAsync(string uriPath)
			=> throw new HttpRequestException("simulated catalogue outage");
	}

	/// <summary>A catalogue that answers from a fixed table, and counts what it was asked.</summary>
	internal sealed class StubSearchNovelsRetriever(params NovelDataDto[] catalogue)
		: NovelReader.Domain.RealTimeReader.Parsing.ISearchNovelsRetriever
	{
		public List<string> RequestedPaths { get; } = [];

		public Task<IReadOnlyCollection<NovelDataDto>> GetNovelsAsync(string uriPath)
		{
			lock (RequestedPaths)
			{
				RequestedPaths.Add(uriPath);
			}

			return Task.FromResult<IReadOnlyCollection<NovelDataDto>>(catalogue);
		}
	}
}
