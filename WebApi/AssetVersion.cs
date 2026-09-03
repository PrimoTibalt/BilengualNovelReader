using System.Security.Cryptography;
using System.Text;

namespace NovelReader
{
	/// <summary>
	/// A short token that changes whenever a served asset changes, so a returning reader can
	/// cache each build forever yet never run a stale one (D25).
	///
	/// It is folded into a *path prefix* — <c>/_v/{token}/…</c> — rather than a query string,
	/// because the reading page is an ES-module graph with no bundler: a query on the entry
	/// script would not reach the relative imports inside it, but a versioned base path does —
	/// <c>./input/pointer.js</c> resolves against the module's own versioned URL.
	/// </summary>
	public sealed class AssetVersion
	{
		public string Token { get; }

		/// <summary>Request path the versioned static files are mounted under.</summary>
		public string PathPrefix => $"/_v/{Token}";

		public AssetVersion(IWebHostEnvironment environment)
		{
			Token = Compute(environment.WebRootPath);
		}

		/// <summary>
		/// A hash of every asset's path, size and last-write time — no file is read. <c>tsc</c>
		/// rewrites the emitted <c>.js</c> on every build, moving their timestamps, so the token
		/// tracks the build without a version anyone has to remember to bump. Computed once at
		/// start-up: a deploy is a restart, which is exactly when it must change.
		/// </summary>
		private static string Compute(string? webRootPath)
		{
			if (string.IsNullOrEmpty(webRootPath) || !Directory.Exists(webRootPath))
			{
				return "dev";
			}

			var fingerprint = new StringBuilder();
			foreach (var file in Directory
				.EnumerateFiles(webRootPath, "*", SearchOption.AllDirectories)
				.OrderBy(path => path, StringComparer.Ordinal))
			{
				var info = new FileInfo(file);
				fingerprint
					.Append(file).Append('|')
					.Append(info.Length).Append('|')
					.Append(info.LastWriteTimeUtc.Ticks).Append('\n');
			}

			var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToString()));
			return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
		}
	}
}
