namespace NovelReader
{
	/// <summary>
	/// The Digital Asset Links statement that lets the Android app open the site full screen, with
	/// no address bar (D51). The app is a Trusted Web Activity: Chrome shows the site as the app
	/// only when the site says, here, that an APK signed with one of these keys speaks for it;
	/// otherwise the app still opens, as a custom tab with the address bar showing.
	///
	/// Android asks for it at the domain's root, <c>/.well-known/assetlinks.json</c>, not under
	/// <c>/reader</c>, so nginx must pass that one path to the app. Configured under
	/// <c>TrustedWebActivity</c>: the package name and the SHA-256 fingerprints of the signing keys
	/// (the upload key, and Google's when Play signs the app). With no fingerprint it is not served.
	/// </summary>
	internal static class AssetLinks
	{
		public const string Path = "/.well-known/assetlinks.json";

		public static void MapAssetLinks(this WebApplication app)
		{
			string? packageName = app.Configuration["TrustedWebActivity:PackageName"];
			string[] fingerprints = app.Configuration.GetSection("TrustedWebActivity:Sha256CertFingerprints").Get<string[]>() ?? [];

			app.MapGet(Path, () =>
			{
				if (string.IsNullOrWhiteSpace(packageName) || fingerprints.Length == 0)
				{
					return Results.NotFound();
				}

				object[] statement =
				[
					new
					{
						relation = new[] { "delegate_permission/common.handle_all_urls" },
						target = new
						{
							@namespace = "android_app",
							package_name = packageName,
							sha256_cert_fingerprints = fingerprints,
						},
					},
				];
				return Results.Json(statement);
			}).AllowAnonymous();
		}
	}
}
