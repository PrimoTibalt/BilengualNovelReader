using Microsoft.AspNetCore.Authentication.Cookies;
using NovelReader.Retrievers;
using NovelReader.Data.Mongo;
using NovelReader.Data.Sqlite;
using NovelReader.Dictionary;
using NovelReader.Domain.RealTimeReader.Reading;
using NovelReader.Domain.RealTimeReader.User;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Primitives;

namespace NovelReader
{
	public class Program
	{
		public static void Main(string[] args)
		{
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

			builder.Services.AddSignalR(options => options.MaximumParallelInvocationsPerClient = 4);

      var app = builder.Build();
      app.UseForwardedHeaders(new() {
          ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor
          });
      app.Use((context, next) =>
          {
          if (context.Request.Headers.TryGetValue("X-Forwarded-Prefix", out StringValues prefix))
          {
            string? firstPrefix = prefix[0];
            if (!string.IsNullOrEmpty(firstPrefix))
            {
              firstPrefix = firstPrefix[0] == '/' ? firstPrefix : string.Concat("/", firstPrefix[0]);
              prefix = new StringValues(
                   [
                   firstPrefix,
                   .. prefix.Select(value => value).TakeLast(prefix.Count-1)
                   ]
              );
              context.Request.PathBase = prefix.ToString();
            }
          }
          return next();
          });

      if (!app.Environment.IsDevelopment())
      {
        app.UseHsts();
      }
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
