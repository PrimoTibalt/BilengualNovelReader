using MongoDB.Driver;
using NovelReader.Domain;

namespace NovelReader.Data.Mongo
{
	internal class FilteredCollection : IFilteredCollection
	{
		internal required IAsyncCursor<Chapter> Value { get; init; }

		public async Task<IChapter?> TryGetExactlyOne()
		{
			if (await Value.MoveNextAsync() && Value.Current.Any())
			{
				return Value.Current.Single();
			}

			return null;
		}
	}
}
