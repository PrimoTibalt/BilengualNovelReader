using HtmlAgilityPack;
using NovelReader.Domain.RealTimeReader.Parsing;

namespace NovelReader.Retrievers
{
	public class ParagraphsRetriever(IHttpClientFactory httpClientFactory) : IParagraphsRetriever
	{
		public async Task<Dictionary<int, string>> GetParagraphsAsync(string uriPath)
		{
			HttpClient httpClient = httpClientFactory.CreateClient("NovelFire");
			HttpResponseMessage response = await httpClient.GetAsync(uriPath);
			response.EnsureSuccessStatusCode();

			HtmlDocument doc = new();
			doc.Load(await response.Content.ReadAsStreamAsync());
			HtmlNodeCollection paragraphNodes = doc.DocumentNode.SelectNodes("//div[@id='content']/p");

			Dictionary<int, string> result = new(paragraphNodes.Count);
			for (var i = 1; i <= paragraphNodes.Count; i++)
			{
				HtmlNode paragraphNode = paragraphNodes[i-1];
				result[i] = paragraphNode.InnerText;
			}

			return result;
		}
	}
}
