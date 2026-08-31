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
			builder.Services.AddSignalR();

			var app = builder.Build();

			app.UseStaticFiles();
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
