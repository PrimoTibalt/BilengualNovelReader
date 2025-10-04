namespace NovelReader.Domain.RealTimeReader.User
{
	public interface IUserDataHandler
	{
		Task UpdateReadingProgressAsync(string userName, ReadingProgress readingProgress);
		Task<ReadingProgress> GetOrCreateUserProgressOnNovelAsync(string userName, string novelName);
	}
}
