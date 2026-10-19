using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using NovelReader.Domain.Monitoring;
using NovelReader.Domain.RealTimeReader.Accounts;
using NovelReader.Domain.RealTimeReader.Review;
using NovelReader.Domain.RealTimeReader.User;
using NovelReader.Domain.RealTimeReader.Vocabulary;
using NovelReader.Monitoring;

namespace NovelReader.Controllers
{
	/// <summary>
	/// The admin panel (D45): its own Angular build, served from <c>admin-ui/</c> beside the
	/// app rather than from <c>wwwroot</c>, so not one of its files can be fetched — or is ever
	/// loaded — by anyone but an admin who opens <c>/Admin</c>. Its JSON is under
	/// <c>/Admin/api</c>. A signed-in reader who is not an admin is answered 404 throughout
	/// (the cookie handler's access-denied event, in Program).
	/// </summary>
	[Route("Admin")]
	[Authorize(Policy = AdminAccess.Policy)]
	public partial class AdminController(
		IWebHostEnvironment environment,
		IMonitoringStore monitoring,
		ResponseTimeLog responseTimes,
		ConnectionTracker connections,
		IUserAccountRepository accounts,
		IVocabularyRepository vocabulary,
		IReviewCardStore reviewCards,
		IReadingProgressStore readingProgress,
		TimeProvider time) : ControllerBase
	{
		/// <summary>Where <c>ng build admin</c> writes, under the content root.</summary>
		public const string OutputDirectory = "admin-ui";

		private const int RecentAuthEvents = 100;
		private const int RecentErrors = 100;

		/// <summary>Anki calls a card mature once its interval reaches three weeks.</summary>
		private const double MatureIntervalDays = 21;

		/// <summary>The graph's ranges: how far back, in slots of how long.</summary>
		private static readonly Dictionary<string, (TimeSpan Span, TimeSpan Slot)> Ranges = new()
		{
			["1h"] = (TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)),
			["6h"] = (TimeSpan.FromHours(6), TimeSpan.FromMinutes(5)),
			["24h"] = (TimeSpan.FromHours(24), TimeSpan.FromMinutes(15)),
			["7d"] = (TimeSpan.FromDays(7), TimeSpan.FromHours(2)),
			["30d"] = (TimeSpan.FromDays(30), TimeSpan.FromHours(8)),
		};

		private static readonly FileExtensionContentTypeProvider ContentTypes = new();

		[GeneratedRegex(@"-[A-Za-z0-9_-]{8}\.(js|css|ttf|woff2?)$")]
		private static partial Regex HashedName { get; }

		[GeneratedRegex("(?<!^)([A-Z])")]
		private static partial Regex WordStart { get; }

		private string Root => Path.Combine(environment.ContentRootPath, OutputDirectory);

		[HttpGet("")]
		public IActionResult Index()
		{
			string shell = Path.Combine(Root, ClientShell.FileName);
			if (!System.IO.File.Exists(shell))
			{
				return NotFound("The admin panel has not been built: npx ng build admin, in WebApi/client.");
			}

			return ClientShell.Serve(this, shell, AdminAccess.PathPrefix);
		}

		/// <summary>
		/// The build's scripts, styles and fonts. Hashed names are cached for good, but only by
		/// this browser (<c>private</c>), since only an admin may have them.
		/// </summary>
		[HttpGet("{**path}")]
		public IActionResult Asset(string path)
		{
			if (path.Equals(ClientShell.FileName, StringComparison.OrdinalIgnoreCase))
			{
				return Index();
			}

			// The provider refuses anything outside its root, and dot-files, by itself.
			using PhysicalFileProvider files = new(Root);
			IFileInfo file = files.GetFileInfo(path);
			if (!file.Exists || file.IsDirectory || file.PhysicalPath is null
				|| !ContentTypes.TryGetContentType(file.Name, out string? contentType))
			{
				return NotFound();
			}

			Response.Headers.CacheControl = HashedName.IsMatch(file.Name)
				? "private, max-age=31536000, immutable"
				: "no-cache";
			return PhysicalFile(file.PhysicalPath, contentType);
		}

		[HttpGet("api/overview")]
		public async Task<AdminOverviewResponse> Overview(CancellationToken cancellationToken)
		{
			DateTime now = time.GetUtcNow().UtcDateTime;

			List<OnlineReaderResponse> online = [.. connections.Snapshot()
				.GroupBy(connection => connection.UserName, StringComparer.OrdinalIgnoreCase)
				.Select(group => new OnlineReaderResponse(
					group.Key,
					[.. group
						.OrderBy(connection => connection.SinceUtc)
						.Select(connection => new OnlineConnectionResponse(
							connection.SinceUtc, connection.LastActiveUtc, connection.Address, connection.UserAgent))]))
				.OrderBy(reader => reader.UserName, StringComparer.OrdinalIgnoreCase)];

			SignInSummaryResponse signIns = new(
				Keyed(await monitoring.CountAuthEventsAsync(now.AddDays(-1), cancellationToken)),
				Keyed(await monitoring.CountAuthEventsAsync(now.AddDays(-7), cancellationToken)),
				[.. (await monitoring.GetAuthEventsAsync(RecentAuthEvents, cancellationToken))
					.Select(authEvent => new AuthEventResponse(
						authEvent.AtUtc, Kebab(authEvent.Outcome), authEvent.UserName, authEvent.Address, authEvent.Detail))]);

			List<ErrorEntryResponse> errors = [.. (await monitoring.GetErrorsAsync(RecentErrors, cancellationToken))
				.Select(error => new ErrorEntryResponse(error.AtUtc, error.Level, error.Category, error.Message, error.Exception))];

			List<ReaderStatsResponse> readers = [];
			foreach (string userName in (await accounts.ListUserNamesAsync(cancellationToken)).Order(StringComparer.OrdinalIgnoreCase))
			{
				readers.Add(await ReaderStatsAsync(userName, now, cancellationToken));
			}

			return new AdminOverviewResponse(now, online, signIns, errors, readers);
		}

		/// <summary>
		/// <paramref name="range"/> is one of 1h, 6h, 24h, 7d, 30d; <paramref name="filter"/> is
		/// <c>hub</c>, <c>http</c> or one operation's name, as the table lists them.
		/// </summary>
		[HttpGet("api/timings")]
		public async Task<IActionResult> Timings(
			[FromQuery] string range = "24h",
			[FromQuery] string filter = ResponseTimeReport.HubFilter,
			CancellationToken cancellationToken = default)
		{
			if (!Ranges.TryGetValue(range, out (TimeSpan Span, TimeSpan Slot) chosen) || filter.Length > 200)
			{
				return BadRequest();
			}

			ResponseTimeReport report = await responseTimes.ReportAsync(chosen.Span, chosen.Slot, filter, cancellationToken);
			return Ok(new ResponseTimesResponse(
				report.FromUtc,
				report.ToUtc,
				(int)report.Slot.TotalMinutes,
				report.Filter,
				[.. report.Series.Select(point => new ResponseTimePointResponse(point.StartUtc, point.Count, point.P50, point.P95))],
				[.. report.Operations.Select(operation =>
					new OperationTimesResponse(operation.Operation, operation.Count, operation.P50, operation.P95))]));
		}

		private async Task<ReaderStatsResponse> ReaderStatsAsync(string userName, DateTime now, CancellationToken cancellationToken)
		{
			IReadOnlyCollection<VocabularyEntry> words = await vocabulary.GetAllForUserAsync(userName, cancellationToken);
			IReadOnlyList<ReviewCard> cards = await reviewCards.GetAllAsync(userName, cancellationToken);

			List<NovelReadResponse> novels = [];
			foreach (NovelSummary novel in await readingProgress.GetNovelsReadAsync(userName, cancellationToken))
			{
				ReadingProgress? progress = await readingProgress.GetAsync(userName, novel.Slug, cancellationToken);
				novels.Add(new NovelReadResponse(
					novel.Slug,
					novel.Title,
					progress?.ChapterNumber,
					progress?.ParagraphNumber,
					novel.TotalChapters,
					progress?.UpdatedAtUtc));
			}

			return new ReaderStatsResponse(
				userName,
				words.Count,
				words.Count(word => word.SavedAtUtc >= now.AddDays(-7)),
				words.Count == 0 ? null : words.Max(word => word.SavedAtUtc),
				new ReviewStatsResponse(
					cards.Count,
					cards.Count(card => card.State == CardState.New),
					cards.Count(card => card.State != CardState.New && card.DueUtc <= now),
					cards.Count(card => card.State is CardState.Learning or CardState.Relearning),
					cards.Count(card => card.State == CardState.Review && card.IntervalDays >= MatureIntervalDays)),
				novels);
		}

		private static Dictionary<string, int> Keyed(IReadOnlyDictionary<AuthOutcome, int> counts) =>
			counts.ToDictionary(pair => Kebab(pair.Key), pair => pair.Value);

		/// <summary><c>SignInFailed</c> → <c>sign-in-failed</c>, as every other code on the wire is written.</summary>
		private static string Kebab(AuthOutcome outcome) =>
			WordStart.Replace(outcome.ToString(), "-$1").ToLowerInvariant();
	}
}
