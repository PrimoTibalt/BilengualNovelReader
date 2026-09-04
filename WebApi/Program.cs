using Microsoft.AspNetCore.Authentication.Cookies;
using NovelReader.Retrievers;
using NovelReader.Data.Mongo;
using NovelReader.Data.Sqlite;
using NovelReader.Dictionary;
using NovelReader.Domain.RealTimeReader.Reading;
using NovelReader.Domain.RealTimeReader.User;

namespace NovelReader
{
	public class Program
	{
		public static void Main(string[] args)
		{
			var builder = WebApplication.CreateBuilder(args);

			builder.Services.RegisterHttpClientAndRetriever();
			builder.Services.RegisterDictionaryProviders();
			builder.Services.AddMongoClient(builder.Configuration);
			builder.Services.RegisterMongoImplementations();
			builder.Services.AddSqliteAccounts(builder.Configuration);
			builder.Services.AddSingleton<IBackgroundWorkScheduler, BackgroundWorkScheduler>();
			builder.Services.AddSingleton<UserRequestGate>();
			builder.Services.AddSingleton<ChapterPreparationService>();
			builder.Services.AddSingleton<ChapterReader>();
			builder.Services.AddSingleton<NovelLibraryService>();
			builder.Services.AddSingleton<AssetVersion>();

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
			// A reader can now have two questions in flight at once: pressing `t` on a phrase asks
			// for the translation and the definition together, and the translation must not wait
			// for the definition to finish (D32). SignalR serialises invocations per connection by
			// default — one at a time — so a phrase, whose definition misses Wiktionary and falls
			// through to the slow second provider (D1), held its translation behind it for seconds.
			// This does not weaken D17: chapters are serialised by the UserRequestGate, not by this.
			builder.Services.AddSignalR(options => options.MaximumParallelInvocationsPerClient = 4);

			var app = builder.Build();

			var assetVersion = app.Services.GetRequiredService<AssetVersion>();

			// Versioned assets: /_v/{token}/… serves the same wwwroot files, but the token
			// changes with every build (D25), so a returning reader fetches fresh URLs while the
			// old ones stay cacheable forever. This mount comes first so its prefix wins.
			app.UseStaticFiles(new StaticFileOptions
			{
				RequestPath = assetVersion.PathPrefix,
				OnPrepareResponse = context =>
					context.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable",
			});

			// Unversioned static files (a direct hit, the favicon) still work, but must
			// revalidate so a stale copy is never used — the versioned URLs above are the path
			// the pages actually reference.
			app.UseStaticFiles(new StaticFileOptions
			{
				OnPrepareResponse = context =>
					context.Context.Response.Headers.CacheControl = "no-cache",
			});

			app.UseHttpsRedirection();

			app.UseAuthentication();
			app.UseAuthorization();

			app.MapControllers().WithStaticAssets();
			app.MapHub<RealTimeReaderHub>("/signalr");

			// Nothing useful lives at the root; send people wherever they belong.
			app.MapGet("/", (HttpContext context) =>
				Results.Redirect(context.User.Identity?.IsAuthenticated == true ? "/ReadingPage" : "/Login"));

			app.Run();
		}
	}
}
