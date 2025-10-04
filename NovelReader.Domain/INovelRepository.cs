namespace NovelReader.Domain
{
	public interface INovelRepository
	{
		Task<ICollectionOfChapters> GetCollectionOfChapters(string novelName);
	}
}
