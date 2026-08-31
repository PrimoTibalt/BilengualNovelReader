using System.Net;

namespace NovelReader.Tests
{
	/// <summary>Returns a canned response for every request, and records what was asked.</summary>
	internal class StubHttpMessageHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
	{
		public int CallCount { get; private set; }

		public Uri? LastRequestUri { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			CallCount++;
			LastRequestUri = request.RequestUri;

			return Task.FromResult(new HttpResponseMessage(statusCode)
			{
				Content = new StringContent(body)
			});
		}
	}

	/// <summary>Handler that always fails, standing in for a provider that is down.</summary>
	internal class FailingHttpMessageHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			throw new HttpRequestException("simulated outage");
		}
	}

	internal class StubHttpClientFactory(HttpMessageHandler handler, string baseAddress) : IHttpClientFactory
	{
		public HttpClient CreateClient(string name)
		{
			return new HttpClient(handler, disposeHandler: false)
			{
				BaseAddress = new Uri(baseAddress)
			};
		}
	}
}
