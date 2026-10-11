namespace NovelReader
{
	/// <summary>
	/// Refuses requests another site's page started (D42): the hub, at any method, and anything
	/// that is not a GET or HEAD. A browser always says where such a request comes from in
	/// <c>Origin</c>, so one that names a different origin is turned away before it reaches
	/// the cookie.
	///
	/// SameSite=Lax already keeps the cookie off requests from other sites, but not from other
	/// pages on this one — <c>/cv</c>, or anything on a subdomain — and a WebSocket opened from
	/// there would arrive signed in as the reader. Requests without <c>Origin</c> come from
	/// something that is not a browser page, which has no reader's cookie to borrow.
	/// </summary>
	internal static class SameOriginRequests
	{
		public static IApplicationBuilder UseSameOriginRequests(this IApplicationBuilder app)
		{
			return app.Use((context, next) =>
			{
				HttpRequest request = context.Request;
				bool guarded = request.Path.StartsWithSegments("/signalr")
					|| !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method));

				if (guarded
					&& request.Headers.Origin is { Count: > 0 } origin
					&& !string.Equals(origin.ToString(), $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase))
				{
					context.Response.StatusCode = StatusCodes.Status403Forbidden;
					return Task.CompletedTask;
				}

				return next();
			});
		}
	}
}
