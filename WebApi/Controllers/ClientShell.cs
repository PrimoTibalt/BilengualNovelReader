using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc;

namespace NovelReader.Controllers
{
	/// <summary>
	/// Serves the Angular client's one HTML shell for both pages, with its <c>&lt;base href&gt;</c>
	/// pointed at the path the app is mounted under (<c>/reader</c> behind nginx). The router
	/// renders the page the URL names; the scripts, the hub and the auth calls all resolve
	/// against that base.
	///
	/// The shell is <c>no-cache</c>, so a returning reader always gets the current build's file
	/// names; the files it names carry a content hash and are cached for good (see
	/// <see cref="ClientAssets"/>).
	/// </summary>
	internal static class ClientShell
	{
		public const string FileName = "index.html";

		private const string DefaultBase = "<base href=\"/\"/>";

		public static IActionResult Serve(ControllerBase controller, IWebHostEnvironment environment)
		{
			string path = Path.Combine(environment.WebRootPath, FileName);
			string pathBase = HtmlEncoder.Default.Encode(controller.Request.PathBase.Value ?? string.Empty);
			string html = System.IO.File
				.ReadAllText(path)
				.Replace(DefaultBase, $"<base href=\"{pathBase}/\"/>");

			controller.Response.Headers.CacheControl = "no-cache";
			return controller.Content(html, "text/html; charset=utf-8");
		}
	}
}
