using System.Diagnostics;
using NovelReader.Domain.Monitoring;

namespace NovelReader.Monitoring
{
	/// <summary>
	/// Times every HTTP request (D45), named by the endpoint it reached — <c>GET ReadingPage</c>,
	/// <c>POST auth/signin</c> — so the operations stay a short, fixed list whatever the URLs.
	/// </summary>
	internal static class ResponseTimeMiddleware
	{
		/// <summary>Files served by <c>UseStaticFiles</c>, which has no endpoint to name it by.</summary>
		public const string StaticFiles = "static files";

		/// <summary>Anything that matched nothing — mostly scanners trying /wp-admin and the like.</summary>
		public const string Unmatched = "unmatched";

		public static IApplicationBuilder UseResponseTimes(this IApplicationBuilder app)
		{
			ResponseTimeLog log = app.ApplicationServices.GetRequiredService<ResponseTimeLog>();

			return app.Use(async (context, next) =>
			{
				// The hub's connection request lasts as long as the page is open; its calls are
				// timed one by one by HubMonitoringFilter instead. Its negotiate is an ordinary request.
				if (context.Request.Path.StartsWithSegments("/signalr", out PathString rest) && rest != "/negotiate")
				{
					await next(context);
					return;
				}

				long started = Stopwatch.GetTimestamp();
				try
				{
					await next(context);
				}
				finally
				{
					log.Record(OperationOf(context), Stopwatch.GetElapsedTime(started));
				}
			});
		}

		private static string OperationOf(HttpContext context)
		{
			if (context.GetEndpoint() is RouteEndpoint endpoint)
			{
				return $"{context.Request.Method} {endpoint.RoutePattern.RawText?.TrimStart('/')}";
			}

			return context.Response.StatusCode < 400 ? StaticFiles : Unmatched;
		}
	}
}
