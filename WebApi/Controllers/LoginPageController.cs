using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace NovelReader.Controllers
{
	[Controller]
	[Route("[controller]")]
	[AllowAnonymous]
	public class LoginController : ControllerBase
	{
		private readonly IWebHostEnvironment _environment;
		private readonly AssetVersion _assetVersion;

		public LoginController(IWebHostEnvironment environment, AssetVersion assetVersion)
		{
			_environment = environment;
			_assetVersion = assetVersion;
		}

		public IActionResult Index()
		{
			return VersionedPage.Serve(this, _environment, _assetVersion, "login.html");
		}
	}
}
