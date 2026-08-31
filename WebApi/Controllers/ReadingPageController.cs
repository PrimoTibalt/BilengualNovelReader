using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NovelReader.Domain.RealTimeReader.Parsing;

namespace NovelReader.Controllers
{
    /// <summary>
    /// Signed-in readers only; anyone else is sent to /Login by the cookie handler.
    /// </summary>
    [Controller]
    [Route("[controller]")]
    [Authorize]
    public class ReadingPageController : ControllerBase
    {
        private ISearchNovelsRetriever _searchNovelsRetriever;

        public ReadingPageController(ISearchNovelsRetriever searchNovelRetriever)
        {
            _searchNovelsRetriever = searchNovelRetriever;
        }

        public IActionResult Index()
        {
            return File("index.html", "text/html");
        }

        [HttpGet("/search/{searchInput}")]
        public async Task<IActionResult> SearchNovels(string searchInput)
        {
            return Ok(
                await _searchNovelsRetriever.GetNovelsAsync(
                    $"/ajax/searchLive?keyword={searchInput}&type=title"
                )
            );
        }
    }
}
