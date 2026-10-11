using System.Text.RegularExpressions;

namespace NovelReader.Domain.RealTimeReader.Reading
{
	/// <summary>
	/// What a novel's name may look like: the source site's slug, e.g. <c>reverend-insanity</c>.
	///
	/// The name comes from the page, and it becomes both a path on the source site
	/// (<c>/book/{name}/chapter-N</c>) and the name of a Mongo collection. Unchecked, a reader
	/// could have the server fetch any path on that site, or create collections without end.
	/// Lower-case letters, digits and dashes cannot do either (D42).
	/// </summary>
	public static partial class NovelSlug
	{
		public const int MaximumLength = 120;

		[GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,119}$")]
		private static partial Regex Pattern { get; }

		public static bool IsValid(string? novelName) =>
			novelName is not null && Pattern.IsMatch(novelName);
	}
}
