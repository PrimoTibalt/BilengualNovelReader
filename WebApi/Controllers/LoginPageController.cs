using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace NovelReader.Controllers
{
	[Controller]
	[Route("[controller]")]
	[AllowAnonymous]
	public class LoginController : ControllerBase
	{
		public IActionResult Index()
		{
			return File("login.html", "text/html");
		}
	}
}
