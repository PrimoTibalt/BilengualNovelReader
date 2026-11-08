using System.Text;
using NovelReader.Domain.RealTimeReader.Books;

namespace NovelReader.Dictionary
{
	/// <summary>
	/// Asks Jev whether a chapter of an uploaded book is part of the book itself, or something
	/// around it — the copyright page, an afterword, a newsletter, a distributor's advert (D52).
	/// One yes/no question per chapter, on its title and opening.
	/// </summary>
	internal sealed class JevBookChapterClassifier(JevClient jev) : IBookChapterClassifier
	{
		/// <summary>
		/// A chapter is hidden only when Jev gives it less than this chance of being part of the book.
		/// Set from both Re:Zero volumes against the live API: see D52.
		/// </summary>
		internal const double MaximumStoryProbability = 0.2;

		private const string Instructions = """
			This is one section of an e-book, as its table of contents lists it: which section it is,
			its title, and how it begins (or, for a section of pictures, how many there are). Is it
			part of the book itself — what a reader bought the book to read or look at?

			Part of the book: the prologue, the chapters, interludes, side stories and the epilogue,
			and the book's own artwork — its cover and the colour illustration inserts.
			Not part of the book: the copyright page, the table of contents, a title page, a
			dedication, acknowledgements, an afterword, the author's or translator's notes and thanks,
			newsletter sign-ups, advertisements, and pages added by the publisher or by whoever
			distributed the file.
			""";

		public async Task<bool?> IsExtraAsync(BookChapterSample sample, CancellationToken cancellationToken = default)
		{
			double? story = await jev.YesProbabilityAsync(
				Describe(sample),
				Instructions,
				"part of the book: story text, or the book's cover or colour illustrations",
				"around the book: copyright, contents, title page, afterword, thanks, newsletter, ads, publisher or distributor page",
				cancellationToken);

			// No answer is not "part of the book": the book is then asked about again later (D53).
			return story is null ? null : story < MaximumStoryProbability;
		}

		internal static string Describe(BookChapterSample sample)
		{
			StringBuilder state = new();
			state.AppendLine($"Book: {sample.BookTitle}");
			state.AppendLine($"Section {sample.Number} of {sample.ChapterCount}: {sample.ChapterTitle}");

			if (sample.Opening.Length == 0)
			{
				state.Append($"No text: {sample.PictureCount} {(sample.PictureCount == 1 ? "picture" : "pictures")}.");
				return state.ToString();
			}

			if (sample.PictureCount > 0)
			{
				state.AppendLine($"({sample.PictureCount} {(sample.PictureCount == 1 ? "picture" : "pictures")} as well)");
			}

			state.AppendLine("It begins:");
			state.Append(sample.Opening);
			return state.ToString();
		}
	}
}
