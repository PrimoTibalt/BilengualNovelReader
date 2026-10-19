using System.Text.RegularExpressions;

namespace NovelReader
{
	/// <summary>
	/// How long the browser may keep each client file. The Angular build names every script,
	/// stylesheet and font after a hash of its content (<c>main-LQ7I5AGI.js</c>), so a file of
	/// that name never changes and is cached for a year; a new build is new names, which the
	/// <c>no-cache</c> shell hands the reader. Anything without a hash must revalidate.
	/// </summary>
	internal static partial class ClientAssets
	{
		[GeneratedRegex(@"-[A-Za-z0-9_-]{8}\.(js|css|ttf|woff2?)$")]
		private static partial Regex HashedName { get; }

		public static IApplicationBuilder UseClientAssets(this IApplicationBuilder app)
		{
			return app.UseStaticFiles(new StaticFileOptions
			{
				OnPrepareResponse = context =>
					context.Context.Response.Headers.CacheControl = HashedName.IsMatch(context.File.Name)
						? "public, max-age=31536000, immutable"
						: "no-cache",
			});
		}
	}
}
