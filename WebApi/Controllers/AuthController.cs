using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NovelReader.Domain.RealTimeReader.Accounts;

namespace NovelReader.Controllers
{
	public record CredentialsRequest(string? UserName, string? Password);

	/// <summary>Answer to a sign-in or sign-up attempt.</summary>
	public record AuthResponse(bool Succeeded, string? UserName, string? Message, string? RedirectTo);

	/// <summary>
	/// Sign-up, sign-in and sign-out. Both entry points answer JSON so the login page can show
	/// a failure in place rather than reloading, and both establish the same cookie.
	/// </summary>
	[ApiController]
	[Route("auth")]
	[AllowAnonymous]
	public class AuthController(AccountService accountService, ILogger<AuthController> logger) : ControllerBase
	{
		private const string ReadingPagePath = "/ReadingPage";

		[HttpPost("signup")]
		public async Task<IActionResult> SignUp([FromBody] CredentialsRequest request, CancellationToken cancellationToken)
		{
			AccountResult result = await accountService.SignUpAsync(request.UserName, request.Password, cancellationToken);
			if (!result.Succeeded)
			{
				// A rejected sign-up is an ordinary outcome, so 400 with the reason.
				return BadRequest(new AuthResponse(false, null, result.Message, null));
			}

			await SignInWithCookieAsync(result.UserName!);
			logger.LogInformation("New account {User}", result.UserName);

			return Ok(new AuthResponse(true, result.UserName, null, ReadingPagePath));
		}

		[HttpPost("signin")]
		public async Task<IActionResult> SignIn([FromBody] CredentialsRequest request, CancellationToken cancellationToken)
		{
			AccountResult result = await accountService.SignInAsync(request.UserName, request.Password, cancellationToken);
			if (!result.Succeeded)
			{
				return Unauthorized(new AuthResponse(false, null, result.Message, null));
			}

			await SignInWithCookieAsync(result.UserName!);
			return Ok(new AuthResponse(true, result.UserName, null, ReadingPagePath));
		}

		[HttpPost("signout")]
		[HttpGet("signout")]
		public async Task<IActionResult> SignOutReader()
		{
			await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
			return Redirect("/Login");
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
