using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NovelReader
{
	/// <summary>
	/// Browser-side protections, sent with every response (D42).
	///
	/// The Content-Security-Policy allows scripts, styles, fonts and connections from this
	/// origin only. It is the backstop for the one place the page writes HTML — the chapter's
	/// paragraphs, which the server escapes (D3): if that ever slipped, an injected script still
	/// would not run. The one inline script, the pre-paint appearance block in
	/// <c>index.html</c>, is allowed by its hash, computed here from the file as it is served,
	/// so editing it needs nothing else changed.
	/// </summary>
	internal static partial class SecurityHeaders
	{
		[GeneratedRegex(@"<script>([\s\S]*?)</script>")]
		private static partial Regex InlineScript { get; }

		public static IApplicationBuilder UseSecurityHeaders(this WebApplication app)
		{
			string inlineScripts = string.Join(' ', InlineScriptHashes(app.Environment.WebRootPath));

			return app.Use((context, next) =>
			{
				// The hub is a WebSocket; spelling the scheme out covers browsers whose 'self'
				// does not yet stretch from https to wss.
				string socket = $"{(context.Request.IsHttps ? "wss" : "ws")}://{context.Request.Host}";

				IHeaderDictionary headers = context.Response.Headers;
				headers.ContentSecurityPolicy = string.Join("; ",
					"default-src 'self'",
					$"script-src 'self' {inlineScripts}".TrimEnd(),
					"style-src 'self'",
					"img-src 'self' data:",
					"font-src 'self'",
					$"connect-src 'self' {socket}",
					"object-src 'none'",
					"base-uri 'self'",
					"form-action 'self'",
					"frame-ancestors 'none'");
				// The pre-CSP spelling of frame-ancestors, for older browsers.
				headers.XFrameOptions = "DENY";
				headers.XContentTypeOptions = "nosniff";
				headers["Referrer-Policy"] = "same-origin";
				// The microphone is the review's: an answer may be spoken rather than typed.
				headers["Permissions-Policy"] = "camera=(), microphone=(self), geolocation=(), payment=()";
				headers["Cross-Origin-Opener-Policy"] = "same-origin";

				return next();
			});
		}

		private static IEnumerable<string> InlineScriptHashes(string webRoot)
		{
			string path = Path.Combine(webRoot, Controllers.ClientShell.FileName);
			if (File.Exists(path))
			{
				foreach (Match script in InlineScript.Matches(File.ReadAllText(path)))
				{
					byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(script.Groups[1].Value));
					yield return $"'sha256-{Convert.ToBase64String(hash)}'";
				}
			}
		}
	}
}
