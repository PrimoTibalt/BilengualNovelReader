using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using NovelReader.Retrievers;
using NovelReader.Data.Mongo;
using NovelReader.Data.Sqlite;
using NovelReader.Dictionary;
using NovelReader.Domain.RealTimeReader.Assistant;
using NovelReader.Domain.RealTimeReader.Reading;
using NovelReader.Domain.RealTimeReader.User;
using NovelReader.Domain.RealTimeReader.Vocabulary;
using Microsoft.AspNetCore.HttpOverrides;

namespace NovelReader
{
	public class Program
	{
		public static void Main(string[] args)
		{
			if (InviteCommand.IsInvoked(args))
			{
				InviteCommand.Run(args);
				return;
			}

			var builder = WebApplication.CreateBuilder(args);

			builder.Services.RegisterHttpClientAndRetriever(builder.Configuration);
			builder.Services.RegisterDictionaryProviders();
			builder.Services.AddMongoClient(builder.Configuration);
			builder.Services.RegisterMongoImplementations();
			builder.Services.AddSqliteAccounts(builder.Configuration);
			builder.Services.AddSingleton<IBackgroundWorkScheduler, BackgroundWorkScheduler>();
			builder.Services.AddSingleton<UserRequestGate>();
			builder.Services.AddSingleton<ChapterPreparationService>();
			builder.Services.AddSingleton<ChapterReader>();
			builder.Services.AddSingleton<NovelLibraryService>();
			builder.Services.AddSingleton<VocabularyBaseForms>();
			builder.Services.AddSingleton<AssetVersion>();

			// Also what the auth cookie is signed with, so persisting the keys keeps readers
			// signed in across restarts as well as keeping their stored API keys readable (D35).
			builder.Services.AddDataProtection()
				.SetApplicationName("NovelReader")
				.PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(
					NovelReader.Data.Sqlite.ServiceCollectionExtension.AccountsDirectory(builder.Configuration),
					"novelreader-keys")));
			builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
			builder.Services.AddSingleton<AssistantSettingsService>();

			builder.Services
				.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
				.AddCookie(options =>
				{
					options.LoginPath = "/Login";
					options.LogoutPath = "/auth/signout";
					options.AccessDeniedPath = "/Login";
					options.ExpireTimeSpan = TimeSpan.FromDays(30);
					options.SlidingExpiration = true;
					options.Cookie.Name = "novelreader.auth";
					options.Cookie.HttpOnly = true;
					options.Cookie.SameSite = SameSiteMode.Lax;
					// Never sent over plain HTTP in production, whatever the proxy reports (D42).
					// Development keeps the http profile working.
					options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
						? CookieSecurePolicy.SameAsRequest
						: CookieSecurePolicy.Always;

					// The page's own fetch and SignalR calls want an answer, not a login page:
					// a hub negotiate that gets HTML back fails with a JSON parse error.
					options.Events.OnRedirectToLogin = context =>
					{
						if (context.Request.Path.StartsWithSegments("/auth")
							|| context.Request.Path.StartsWithSegments("/signalr"))
						{
							context.Response.StatusCode = StatusCodes.Status401Unauthorized;
							return Task.CompletedTask;
						}

						context.Response.Redirect(context.RedirectUri);
						return Task.CompletedTask;
					};
				});

			builder.Services.AddAuthorization();
			builder.Services.AddControllersWithViews();

			builder.Services.AddSignalR(options => options.MaximumParallelInvocationsPerClient = 4);
			builder.Services.AddAuthRateLimits();

			// nginx, on this machine, says who the client was, over which scheme, and under which
			// path the app is mounted (/reader). Only loopback is trusted to say so — the default —
			// so a client's own X-Forwarded-* headers are ignored (D42).
			builder.Services.Configure<ForwardedHeadersOptions>(options =>
				options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
					| ForwardedHeaders.XForwardedProto
					| ForwardedHeaders.XForwardedPrefix);

			var app = builder.Build();

			// Built now rather than by the first sign-in, so that request does not pay for the
			// dummy hash an unknown name is checked against — a one-off delay that would mark
			// the name as unknown (D41).
			app.Services.GetRequiredService<NovelReader.Domain.RealTimeReader.Accounts.AccountService>();

			// First, so everything after sees the real client address, scheme and path base.
			app.UseForwardedHeaders();
			app.UseSecurityHeaders();
			app.UseSameOriginRequests();

			// No UseHsts: nginx sends Strict-Transport-Security for the whole domain, and a
			// second, shorter header from here only contradicted it (D42).
      string? assetVersion = app.Services.GetRequiredService<AssetVersion>().PathPrefix;
			// Versioned assets: /_v/{token}/… serves the same wwwroot files, but the token
			// changes with every build (D25), so a returning reader fetches fresh URLs while the
			// old ones stay cacheable forever. This mount comes first so its prefix wins.
			app.UseStaticFiles(new StaticFileOptions
			{
				RequestPath = assetVersion,
				OnPrepareResponse = context =>
					context.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable",
			});

			app.UseStaticFiles(new StaticFileOptions
			{
				OnPrepareResponse = context =>
					context.Context.Response.Headers.CacheControl = "no-cache",
			});

			app.UseAuthentication();
			app.UseAuthorization();
			app.UseRateLimiter();

			app.MapControllers().WithStaticAssets();
			app.MapHub<RealTimeReaderHub>("/signalr");

			app.MapGet("/", (HttpContext context) => {
        string uri = context.Request.PathBase + (context.User.Identity?.IsAuthenticated == true ? "/ReadingPage" : "/Login");
        return Results.Redirect(uri);
      });

			app.Run();
		}
	}
}
