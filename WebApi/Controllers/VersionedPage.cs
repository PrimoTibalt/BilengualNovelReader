using Microsoft.AspNetCore.Mvc;

namespace NovelReader.Controllers
{
	/// <summary>
	/// Serves one of the static HTML shells with a <c>&lt;base href&gt;</c> pointing at the
	/// versioned asset path, so the shell's own relative asset URLs — and the module graph they
	/// pull in — resolve under <c>/_v/{token}/…</c> and cache-bust with every build (D25).
	///
	/// The shell itself is marked <c>no-cache</c> so a returning reader always revalidates it
	/// and picks up the current token; the versioned assets it then names are cached forever.
	/// </summary>
	internal static class VersionedPage
	{
		public static IActionResult Serve(
			ControllerBase controller,
			IWebHostEnvironment environment,
			AssetVersion version,
			string fileName)
		{
			var path = Path.Combine(environment.WebRootPath, fileName);
			var html = System.IO.File
				.ReadAllText(path)
				.Replace("<!--ASSET-BASE-->", $"<base href=\"{version.PathPrefix}/\">");

			controller.Response.Headers.CacheControl = "no-cache";
			return controller.Content(html, "text/html; charset=utf-8");
		}
	}
}
