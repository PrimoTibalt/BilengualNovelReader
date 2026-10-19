using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using NovelReader.Domain.Monitoring;

namespace NovelReader.Monitoring
{
	/// <summary>
	/// Times every hub call and keeps <see cref="ConnectionTracker"/> up to date (D45). The
	/// reading page does nearly everything over the hub's one long-lived connection, so the
	/// HTTP middleware alone would see none of it.
	/// </summary>
	internal sealed class HubMonitoringFilter(ResponseTimeLog responseTimes, ConnectionTracker connections, TimeProvider time) : IHubFilter
	{
		/// <summary>A user agent is cut to this; the panel only needs to tell a phone from a laptop.</summary>
		private const int MaxUserAgentLength = 300;

		public async ValueTask<object?> InvokeMethodAsync(
			HubInvocationContext invocationContext,
			Func<HubInvocationContext, ValueTask<object?>> next)
		{
			connections.Touch(invocationContext.Context.ConnectionId, time.GetUtcNow().UtcDateTime);

			long started = Stopwatch.GetTimestamp();
			try
			{
				return await next(invocationContext);
			}
			finally
			{
				responseTimes.Record(
					ResponseTimeReport.HubPrefix + invocationContext.HubMethodName,
					Stopwatch.GetElapsedTime(started));
			}
		}

		public Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
		{
			HttpContext? http = context.Context.GetHttpContext();
			string? userAgent = http?.Request.Headers.UserAgent.ToString();
			connections.Opened(context.Context.ConnectionId, new OnlineConnection(
				context.Context.User?.Identity?.Name ?? "?",
				time.GetUtcNow().UtcDateTime,
				http?.Connection.RemoteIpAddress?.ToString(),
				userAgent is { Length: > MaxUserAgentLength } ? userAgent[..MaxUserAgentLength] : userAgent));

			return next(context);
		}

		public Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
		{
			connections.Closed(context.Context.ConnectionId);
			return next(context, exception);
		}
	}
}
