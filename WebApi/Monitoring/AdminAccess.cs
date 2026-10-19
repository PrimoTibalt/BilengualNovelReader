using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace NovelReader.Monitoring
{
	/// <summary>
	/// Who may open the admin panel (D45): the account names under <c>Admin:Users</c>, compared
	/// case-insensitively as accounts are. Anyone else signed in is answered 404, so the panel
	/// does not show that it exists; anyone signed out is sent to sign in, as for any page.
	/// </summary>
	internal sealed class AdminAccess(IConfiguration configuration)
	{
		public const string Policy = "admin";

		/// <summary>Everything the panel serves lives under this path.</summary>
		public const string PathPrefix = "/Admin";

		/// <summary>Its JSON, which answers 401 rather than redirecting to sign in.</summary>
		public const string ApiPrefix = "/Admin/api";

		private readonly HashSet<string> admins = new(
			configuration.GetSection("Admin:Users").Get<string[]>() ?? [],
			StringComparer.OrdinalIgnoreCase);

		public bool IsAdmin(ClaimsPrincipal user) =>
			user.Identity?.IsAuthenticated == true
			&& user.Identity.Name is { } name
			&& admins.Contains(name);

		public static void AddAdminPolicy(IServiceCollection services, AdminAccess access)
		{
			services.AddSingleton(access);
			services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy
				.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme)
				.RequireAuthenticatedUser()
				.RequireAssertion(context => access.IsAdmin(context.User)));
		}
	}
}
