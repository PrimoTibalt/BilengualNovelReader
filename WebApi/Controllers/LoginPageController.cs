using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace NovelReader.Controllers
{
	[Controller]
	[Route("[controller]")]
	[AllowAnonymous]
	public class LoginController(IWebHostEnvironment environment) : ControllerBase
	{
		public IActionResult Index() => ClientShell.Serve(this, environment);
	}
}
