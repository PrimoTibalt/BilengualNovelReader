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
        private readonly IWebHostEnvironment _environment;

        public ReadingPageController(
            ISearchNovelsRetriever searchNovelRetriever,
            IWebHostEnvironment environment)
        {
            _searchNovelsRetriever = searchNovelRetriever;
            _environment = environment;
        }

        public IActionResult Index()
        {
            return ClientShell.Serve(this, _environment);
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
