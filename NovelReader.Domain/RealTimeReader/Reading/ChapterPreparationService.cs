using NovelReader.Domain.RealTimeReader.Parsing;
using NovelReader.Domain.RealTimeReader.Vocabulary;

namespace NovelReader.Domain.RealTimeReader.Reading
{
    /// <summary>
    /// Turns a chapter into reader-ready markup: fetch the raw paragraphs (scraping on a
    /// cache miss), then wrap every word this reader has saved.
    /// </summary>
    public class ChapterPreparationService(
        INovelRepository novelRepository,
        IParagraphsRetriever paragraphsRetriever,
        IVocabularyRepository vocabularyRepository
    )
    {
        public async Task<PreparedChapter> PrepareAsync(
            string userName,
            string novelName,
            int chapterNumber,
            CancellationToken cancellationToken = default
        )
        {
            IChapter rawChapter = await GetOrCreateChapterAsync(novelName, chapterNumber);
            IReadOnlySet<string> knownTerms = await GetKnownTermsAsync(userName, cancellationToken);

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

        public async Task<IReadOnlySet<string>> GetKnownTermsAsync(
            string userName,
            CancellationToken cancellationToken = default
        )
        {
            IReadOnlyCollection<VocabularyEntry> entries =
                await vocabularyRepository.GetAllForUserAsync(userName, cancellationToken);

            HashSet<string> knownTerms = new(entries.Count);
            foreach (VocabularyEntry entry in entries)
            {
                if (entry.NormalizedTerm.Length > 0)
                {
                    knownTerms.Add(entry.NormalizedTerm);
                }
            }

            return knownTerms;
        }

        private async Task<IChapter> GetOrCreateChapterAsync(string novelName, int chapterNumber)
        {
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
                paragraphsRetriever.GetParagraphsAsync($"/book/{novelName}/chapter-{chapterNumber}")
            );

            return await chaptersCollection.InsertOneAsync(chapterNumber, paragraphsLazy);
        }
    }
}
