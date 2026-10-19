using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using NovelReader.Controllers;
using NovelReader.Domain.Monitoring;
using NovelReader.Monitoring;

namespace NovelReader
{
	/// <summary>
	/// Limits per client address on the two endpoints that hash a password (D42). The
	/// per-name lock (D41) stops guessing at one account; this stops one address from guessing
	/// at many, and from keeping the CPU busy with PBKDF2 — each attempt costs about 125 ms of it.
	///
	/// The address is the one <c>UseForwardedHeaders</c> took from nginx, so it must run first;
	/// without it every request would come from 127.0.0.1 and share one budget.
	/// </summary>
	internal static class AuthRateLimits
	{
		public const string SignIn = "sign-in";
		public const string SignUp = "sign-up";

		public static void AddAuthRateLimits(this IServiceCollection services)
		{
			services.AddRateLimiter(options =>
			{
				options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

				// Ten tries a minute is far more than a person mistyping needs.
				options.AddPolicy(SignIn, context => RateLimitPartition.GetFixedWindowLimiter(
					ClientAddress(context),
					_ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));

				// Invites are single-use (D42), so a person signs up once; five an hour leaves
				// room for typos in a code and nothing for guessing codes.
				options.AddPolicy(SignUp, context => RateLimitPartition.GetFixedWindowLimiter(
					ClientAddress(context),
					_ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromHours(1) }));

				// The login page shows the message of whatever JSON comes back, so say it in its shape.
				options.OnRejected = async (rejected, cancellationToken) =>
				{
					string wait = "a minute";
					if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
					{
						rejected.HttpContext.Response.Headers.RetryAfter =
							((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
						int minutes = (int)Math.Ceiling(retryAfter.TotalMinutes);
						wait = minutes <= 1 ? "a minute" : $"{minutes} minutes";
					}

					// No name: the body is not read for a refused request, which is the point of refusing it.
					await rejected.HttpContext.RequestServices.GetRequiredService<AuthEventRecorder>().RecordAsync(
						AuthOutcome.RateLimited, null, rejected.HttpContext, rejected.HttpContext.Request.Path.Value);

					await rejected.HttpContext.Response.WriteAsJsonAsync(
						new AuthResponse(false, null, $"Too many attempts from your network. Try again in {wait}.", null),
						cancellationToken);
				};
			});
		}

		private static string ClientAddress(HttpContext context) =>
			context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
	}
}
