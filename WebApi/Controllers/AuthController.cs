using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NovelReader.Domain.Monitoring;
using NovelReader.Domain.RealTimeReader.Accounts;
using NovelReader.Monitoring;

namespace NovelReader.Controllers
{
	/// <summary><see cref="InviteCode"/> is read by sign-up only (D42).</summary>
	public record CredentialsRequest(string? UserName, string? Password, string? InviteCode = null);

	/// <summary>Answer to a sign-in or sign-up attempt.</summary>
	public record AuthResponse(bool Succeeded, string? UserName, string? Message, string? RedirectTo);

	/// <summary>
	/// Sign-up, sign-in and sign-out. Both entry points answer JSON so the login page can show
	/// a failure in place rather than reloading, and both establish the same cookie.
	/// </summary>
	[ApiController]
	[Route("auth")]
	[AllowAnonymous]
	// A name, a password and an invite code fit in far less; anything bigger is not a sign-in.
	[RequestSizeLimit(4096)]
	public class AuthController(AccountService accountService, AuthEventRecorder authEvents, ILogger<AuthController> logger) : ControllerBase
	{
		private const string ReadingPagePath = "/ReadingPage";

		[HttpPost("signup")]
		[EnableRateLimiting(AuthRateLimits.SignUp)]
		public async Task<IActionResult> SignUp([FromBody] CredentialsRequest request, CancellationToken cancellationToken)
		{
			AccountResult result = await accountService.SignUpAsync(
				request.UserName, request.Password, request.InviteCode, cancellationToken);
			if (!result.Succeeded)
			{
				await authEvents.RecordAsync(AuthOutcome.SignUpRefused, request.UserName, HttpContext, result.Failure.ToString());
				// A rejected sign-up is an ordinary outcome, so 400 with the reason.
				return BadRequest(new AuthResponse(false, null, result.Message, null));
			}

			await SignInWithCookieAsync(result.UserName!);
			logger.LogInformation("New account {User}", result.UserName);
			await authEvents.RecordAsync(AuthOutcome.SignedUp, result.UserName, HttpContext);

			return Ok(new AuthResponse(true, result.UserName, null, ReadingPagePath));
		}

		[HttpPost("signin")]
		[EnableRateLimiting(AuthRateLimits.SignIn)]
		public async Task<IActionResult> SignIn([FromBody] CredentialsRequest request, CancellationToken cancellationToken)
		{
			AccountResult result = await accountService.SignInAsync(request.UserName, request.Password, cancellationToken);
			if (result.Failure == AccountFailure.LockedOut)
			{
				// Said as plainly for a name that does not exist as for one that does (D41).
				logger.LogWarning("Sign-in locked for {User} ({Minutes} min left)", request.UserName, result.RetryAfterMinutes);
				await authEvents.RecordAsync(AuthOutcome.SignInLocked, request.UserName, HttpContext, $"{result.RetryAfterMinutes} min left");
				Response.Headers.RetryAfter = ((int)Math.Ceiling(result.RetryAfter!.Value.TotalSeconds)).ToString();
				return StatusCode(StatusCodes.Status429TooManyRequests, new AuthResponse(false, null, result.Message, null));
			}

			if (!result.Succeeded)
			{
				// Recorded alike for an unknown name and a wrong password, so neither takes
				// longer than the other (D41).
				await authEvents.RecordAsync(AuthOutcome.SignInFailed, request.UserName, HttpContext);
				return Unauthorized(new AuthResponse(false, null, result.Message, null));
			}

			await SignInWithCookieAsync(result.UserName!);
			await authEvents.RecordAsync(AuthOutcome.SignedIn, result.UserName, HttpContext);
			return Ok(new AuthResponse(true, result.UserName, null, ReadingPagePath));
		}

		/// <summary>
		/// POST only (D42): as a GET, any page on the web could sign a reader out with an image
		/// tag. The page posts here and then goes to the login screen itself.
		/// </summary>
		[HttpPost("signout")]
		public async Task<IActionResult> SignOutReader()
		{
			await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
			return NoContent();
		}

		/// <summary>Who the caller is, so the login page can skip itself when already signed in.</summary>
		[HttpGet("me")]
		public IActionResult Me()
		{
			string? name = User.Identity?.IsAuthenticated == true ? User.Identity.Name : null;
			return Ok(new AuthResponse(name is not null, name, null, name is not null ? ReadingPagePath : null));
		}

		private Task SignInWithCookieAsync(string userName)
		{
			// Name is the only claim: it is the key every other store is written against.
			ClaimsIdentity identity = new(
				[new Claim(ClaimTypes.Name, userName)],
				CookieAuthenticationDefaults.AuthenticationScheme);

			return HttpContext.SignInAsync(
				CookieAuthenticationDefaults.AuthenticationScheme,
				new ClaimsPrincipal(identity),
				new AuthenticationProperties { IsPersistent = true });
		}
	}
}
