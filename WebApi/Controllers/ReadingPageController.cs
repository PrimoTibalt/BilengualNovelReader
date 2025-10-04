using Microsoft.AspNetCore.Mvc;

namespace NovelReader.Controllers
{
	[Controller]
	[Route("[controller]")]
	public class ReadingPageController : ControllerBase
	{
		public IActionResult Index()
		{
			return File("index.html", "text/html");
		}
	}
}