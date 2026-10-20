using NovelReader.Domain.RealTimeReader.Parsing;
using NovelReader.Domain.RealTimeReader.Vocabulary;

namespace NovelReader.Domain.RealTimeReader.Reading
{
    /// <summary>
    /// Turns a chapter into reader-ready markup: fetch the raw paragraphs (scraping on a
    /// cache miss), then wrap every word this reader has saved. A scraped page is put to the
    /// <c>chapterCheck</c>, when there is one, before it is stored (D47).
    /// </summary>
    public class ChapterPreparationService(
        INovelRepository novelRepository,
        IParagraphsRetriever paragraphsRetriever,
        IVocabularyRepository vocabularyRepository,
        IChapterTextCheck? chapterCheck = null
    )
    {
        /// <summary>
        /// The waits between attempts at a chapter the site did not serve (D49). Its random
        /// redirect hit about one request in six when measured, so three attempts all failing
        /// is rare; a chapter that is really gone answers "not found" and is not retried.
        /// </summary>
        public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

        public async Task<PreparedChapter> PrepareAsync(
            string userName,
            string novelName,
            int chapterNumber,
            CancellationToken cancellationToken = default
        )
        {
            IChapter rawChapter = await GetOrCreateChapterAsync(novelName, chapterNumber);
            IReadOnlyDictionary<string, string> knownTerms = await GetKnownTermsAsync(userName, cancellationToken);

            Dictionary<int, string> markedUpParagraphs = [];
            foreach ((string key, object value) in rawChapter.Paragraphs)
            {
                // Paragraph keys are stringified numbers; anything else is a stray Bson field.
                if (!int.TryParse(key, out int paragraphNumber) || value is not string text)
                {
                    continue;
                }

                markedUpParagraphs[paragraphNumber] = ParagraphMarkupBuilder.BuildMarkup(
                    text,
                    knownTerms
                );
            }

            return new PreparedChapter
            {
                NovelName = novelName,
                ChapterNumber = chapterNumber,
                Paragraphs = markedUpParagraphs,
                PreparedAtUtc = DateTime.UtcNow,
            };
        }

        /// <summary>Every form of every saved word, mapped to the word as saved (D39).</summary>
        public async Task<IReadOnlyDictionary<string, string>> GetKnownTermsAsync(
            string userName,
            CancellationToken cancellationToken = default
        )
        {
            IReadOnlyCollection<VocabularyEntry> entries =
                await vocabularyRepository.GetAllForUserAsync(userName, cancellationToken);

            return new VocabularyForms(entries).SavedTermByForm;
        }

        private async Task<IChapter> GetOrCreateChapterAsync(string novelName, int chapterNumber)
        {
            // The hub checks this already; here because this is where the name turns into a
            // collection and a URL, and nothing should reach that without passing it (D42).
            if (!NovelSlug.IsValid(novelName))
            {
                throw new ArgumentException($"'{novelName}' is not a novel name.", nameof(novelName));
            }

            ICollectionOfChapters chaptersCollection =
                await novelRepository.GetCollectionOfChapters(novelName);
            IFilteredCollection filtered = await chaptersCollection.FilterByChapter(chapterNumber);

            IChapter? existing = await filtered.TryGetExactlyOne();
            if (existing is not null)
            {
                return existing;
            }

            // The scrape only happens when the chapter is genuinely not stored yet.
            Lazy<Task<Dictionary<int, string>>> paragraphsLazy = new(() =>
                ScrapeAsync($"/book/{novelName}/chapter-{chapterNumber}")
            );

            return await chaptersCollection.InsertOneAsync(chapterNumber, paragraphsLazy);
        }

        private async Task<Dictionary<int, string>> ScrapeAsync(string uriPath)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await ScrapeOnceAsync(uriPath);
                }
                catch (Exception exception) when (IsWorthRetrying(exception) && attempt < RetryDelays.Count)
                {
                    await Task.Delay(RetryDelays[attempt]);
                }
            }
        }

        private async Task<Dictionary<int, string>> ScrapeOnceAsync(string uriPath)
        {
            Dictionary<int, string> paragraphs = await paragraphsRetriever.GetParagraphsAsync(uriPath);
            if (chapterCheck is not null && await chapterCheck.IsSitePageAsync(paragraphs))
            {
                // Thrown so that nothing caches it, and as "unavailable" so that it is asked for
                // again: the site's own pages come and go like its redirect does.
                throw new ChapterUnavailableException($"'{uriPath}' is a page of the site's, not a chapter.");
            }

            return paragraphs;
        }

        /// <summary>Anything but the site saying the chapter does not exist: a timeout and a dropped connection pass too.</summary>
        private static bool IsWorthRetrying(Exception exception) =>
            exception is ChapterUnavailableException or HttpRequestException or TaskCanceledException;
    }
}
