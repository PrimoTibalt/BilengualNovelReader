using Microsoft.AspNetCore.SignalR;
using NovelReader.Domain.RealTimeReader.Reading;
using NovelReader.Domain.RealTimeReader.User;

namespace NovelReader
{
	public class RealTimeReaderHub(NextParagraphProcessor nextParagraphProcessor, IUserDataHandler userDataHandler) : Hub
	{
		public async Task GetNextParagraph(string userName, string novelName, int chapterNumber, int paragraphNumber)
		{
			var (newChapterNumber,newParagraphNumber,content) = await nextParagraphProcessor.ProcessAndReturnAsync(novelName, chapterNumber, paragraphNumber);
			ReadingProgress readingProgress = new()
			{
				NovelName = novelName,
				ChapterNumber = newChapterNumber,
				ParagraphNumber = newParagraphNumber
			};
			await Clients.Caller.SendAsync("ReturnNextParagraph", newChapterNumber, newParagraphNumber + 1, content);
			await userDataHandler.UpdateReadingProgressAsync(userName, readingProgress);
		}
	}
}
