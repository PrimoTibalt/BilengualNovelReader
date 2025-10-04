namespace NovelReader.Domain
{
	public interface IFilteredCollection
	{
		Task<IChapter?> TryGetExactlyOne();
	}
}
