# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
./run.sh                              # start everything: Mongo, SignalR client, the app
./run.sh --https                      # same, on the https profile
./run.sh --stop                       # stop the Mongo container
dotnet run --project WebApi -- invite # print a single-use, 24-hour sign-up invite code (D42)
dotnet build NovelReader.sln          # build all four projects
dotnet run --project WebApi           # run the web app (http profile -> http://localhost:5261)
dotnet run --project WebApi --launch-profile https   # https://localhost:7178
```

`run.sh` is the one-command path: it handles the prerequisites below (podman Mongo and the
build) and then runs the app in the foreground. The
Mongo container is left running after Ctrl-C so cached chapters survive a restart.

The Angular client (`WebApi/client`) is built by `dotnet build` (the `BuildClient` target in `WebApi/NovelReader.csproj` runs `npm ci` and `ng build` into `WebApi/wwwroot`), so it needs no separate step. To iterate on the frontend alone, from `WebApi/client/`:

```bash
npx ng build                                  # build into ../wwwroot (type-checks the templates too)
npx ng build admin                            # the admin panel, into ../admin-ui (D45)
npx ng build --watch --configuration development
dotnet build -p:SkipClientBuild=true          # from WebApi/: reuse what is already in wwwroot
```

`wwwroot/` and `admin-ui/` are entirely build output — every client build wipes and rewrites them — and are gitignored.
Static files are re-read per request, so a client rebuild shows on reload; only an edit to
the inline script in `index.html` needs a restart (its CSP hash is computed at start-up).

Tests: `dotnet test NovelReader.Tests` (xunit). No lint/format config.

### Prerequisites the build does not cover

- **ASP.NET Core runtime + targeting pack.** The projects target `net10.0` and `WebApi` uses `Microsoft.NET.Sdk.Web`, which needs the ASP.NET Core shared framework — it is *not* part of the base .NET SDK on Arch. Without the targeting pack the build fails with `NETSDK1226: Prune Package data not found ... Microsoft.AspNetCore.App`; without the runtime the app fails at launch with `No frameworks were found`. Install both:
  ```bash
  sudo pacman -S aspnet-runtime aspnet-targeting-pack
  ```
  `-p:AllowMissingPrunePackageData=true` will get a build through without the targeting pack, but the app still will not run — don't bake that flag into the project files.

- **MongoDB must be reachable** at the `DefaultConnectionString` in `WebApi/appsettings.json` (`mongodb://admin:password@127.0.0.1:27017`). It is not in the Arch repos; a rootless container matching those credentials:
  ```bash
  podman run -d --name novelreader-mongo -p 27017:27017 \
    -e MONGO_INITDB_ROOT_USERNAME=admin -e MONGO_INITDB_ROOT_PASSWORD=password \
    -v novelreader-mongo-data:/data/db docker.io/library/mongo:8
  ```
  A client certificate is used only when `MongoOptions:X509Certificate` (and `…Pass`) is
  configured — Atlas; without one the connection string's credentials are used.
  `AddMongoClient` pings on startup but only logs failures to the console, so a missing Mongo does not stop the app booting — it surfaces later as a timeout inside the hub. A healthy start prints `Pinged your deployment.`

## Architecture

Five projects; dependencies point inward toward `NovelReader.Domain`, which has **zero package references** and holds only interfaces, POCOs and pure logic (password hashing lives there because PBKDF2 is BCL, not NuGet).

- `NovelReader.Domain` — interfaces (`INovelRepository`, `ICollectionOfChapters`, `IFilteredCollection`, `IChapter`, `IParagraphsRetriever`, `IUserDataHandler`), plus `NextParagraphProcessor`, the single piece of business logic.
- `NovelReader.Retrievers` — `ParagraphsRetriever` scrapes chapter paragraphs from novelfire.net (`//div[@id='content']/p`) with HtmlAgilityPack over a named `HttpClient` ("NovelFire", browser User-Agent).
- `NovelReader.Data.Mongo` — MongoDB implementations. **All classes here are `internal`**; the only public surface is `ServiceCollectionExtension`.
- `NovelReader.Data.Sqlite` — user accounts in a local SQLite file, same convention: everything `internal` bar `ServiceCollectionExtension`, with `InternalsVisibleTo` for the tests.
- `WebApi` (project/assembly name `NovelReader`, root namespace `NovelReader`) — ASP.NET Core host, SignalR hub, static frontend.

Each infrastructure project owns its own `ServiceCollectionExtension`; `WebApi/Program.cs` just calls `RegisterHttpClientAndRetriever()`, `AddMongoClient(configuration)`, `RegisterMongoImplementations()`, `AddSqliteAccounts(configuration)`. Adding a new infrastructure concern means adding an extension there, not wiring types individually in `Program.cs`.

### The repository abstraction mirrors the Mongo driver

`INovelRepository -> ICollectionOfChapters -> IFilteredCollection -> IChapter` is a deliberate step-by-step wrapper over `MongoClient -> IMongoCollection -> IAsyncCursor -> document`, so the domain can drive a query pipeline without referencing `MongoDB.Driver`. Keep that shape when extending: a new query is a new interface method returning another domain abstraction, never a driver type leaking into `NovelReader.Domain`.

### Authentication

Readers sign in; the hub takes the reader from the cookie and **no hub method accepts a user
name** (D20). A client that could name itself could read and rewrite anyone's progress and
vocabulary.

- Accounts: SQLite, one `accounts` table, `COLLATE NOCASE` primary key so usernames are unique
  case-insensitively. PBKDF2-HMAC-SHA256 via `PasswordHasher`; the stored string carries its
  own algorithm, iteration count and salt.
- **Sign-up needs an invite code** (D42), made on the server with `NovelReader invite` (from
  the app's directory and environment, so it opens the same database). Codes are single-use,
  24 hours, stored as SHA-256 in an `invites` table, and spent in the same transaction that
  creates the account.
- `/auth/signin` and `/auth/signup` are rate-limited per client address (`AuthRateLimits`:
  10/min, 5/hour), which needs `UseForwardedHeaders` first so the address is the client's
  and not nginx's. Passwords are 8–128 characters. Sign-out is POST-only.
- `/Login` and `/auth/*` are anonymous; `/ReadingPage` and the hub are not. A cookie challenge
  on `/auth` or `/signalr` answers **401 rather than redirecting** — a hub negotiate that gets
  the login page back fails with a JSON parse error instead of a useful one.
- `AccountService` is where the rules live (name shape, password length, and answering
  "unknown user" and "wrong password" identically, at the same cost — the dummy hash an unknown
  name is checked against is built at start-up, so not even the first request differs (D41)).

### Request hardening (D42)

`Program.cs` runs, in order: `UseForwardedHeaders` (X-Forwarded-For/Proto/Prefix, trusted
from loopback only — nginx), `UseSecurityHeaders`, `UseSameOriginRequests`, and after
authorization `UseRateLimiter`. Things that break if forgotten:

- **The CSP allows scripts and styles from this origin only.** The inline `<script>` in the
  built `index.html` is allowed by a hash computed from the file at start-up, so editing it
  needs a restart, nothing more. An inline `style="…"` attribute, a component `styles:` block
  (Angular injects those as `<style>` tags) or a script from a CDN will be blocked: styles go
  in the global stylesheets under `client/src/styles/`, and dynamic values through
  `element.style`/`[style.x]` bindings (CSSOM, which the CSP allows). The build's
  `inlineCritical` is off for the same reason.
- **The Permissions-Policy allows the microphone for this origin** — the review's dictation.
- **A request from another origin is refused with 403**: the hub, and any POST/PUT/DELETE.
  A new page that posts from somewhere other than this origin will not work.
- **Novel names are checked against `NovelSlug`** in the hub. A new hub method that takes a
  novel name must check it too.
- `AllowedHosts` lists the host names the app answers to.

### The admin panel (D45)

`/Admin`, for the account names under `Admin:Users` in appsettings (`anton`). A **second
Angular application**, `client/projects/admin`, built into `WebApi/admin-ui/` — deliberately
**not** `wwwroot`, so `UseStaticFiles` cannot serve it. `AdminController` serves its shell, its
files (`/Admin/{**path}`, through a `PhysicalFileProvider` rooted at `admin-ui`) and its JSON
(`/Admin/api/overview`, `/Admin/api/timings`), all behind the `admin` policy. Signed out:
redirected to sign in (the API answers 401). Signed in but not an admin: **404**, from the
cookie handler's `OnRedirectToAccessDenied`, so the panel does not reveal itself. The
reader's app must never link to it or import from it.

Sources, in `WebApi/Monitoring/` unless noted:
- **Response times.** `ResponseTimeMiddleware` (right after `UseForwardedHeaders`) times HTTP
  requests, named by endpoint (`POST auth/signin`), else `static files`/`unmatched`; it
  skips the hub's long-lived connection request. `HubMonitoringFilter` times each hub call as
  `hub <Method>`. Both record into `ResponseTimeLog` (Domain/Monitoring): per-minute,
  per-operation `LatencyHistogram`s, log-scale buckets that merge by adding, so p50/p95
  of any range is a sum of minutes. **Keep operation names a short fixed list**: never put a
  URL, a user or an id in one.
- **Sign-in attempts.** `AuthEventRecorder`, called from `AuthController`, the rate limiter's
  `OnRejected` and the hub's `ChangePassword`. A failed sign-in is recorded the same way
  whether the name exists or not (D41). Recording failures are logged, never thrown.
- **Errors.** `ErrorLog`, a logging provider that queues warnings and errors (with the
  exception text) for `MonitoringWriter`. It ignores `MonitoringWriter`'s own category so a
  broken store cannot feed itself.
- **Online.** `ConnectionTracker`, kept by the hub filter's connect/disconnect, in memory.
- **Readers.** Read live from the vocabulary, review-card and progress stores, per account
  (`IUserAccountRepository.ListUserNamesAsync`).

`MonitoringWriter` (a hosted service) flushes finished minutes and queued errors every 10 s,
everything at shutdown, and prunes hourly (timings and errors 30 days, sign-ins 90).

### Read path (the core flow)

`RealTimeReaderHub.LoadChapter(novelName, chapterNumber)` → `ChapterReader.LoadChapterAsync`:

1. Take the reader's turn on the `UserRequestGate` — one reader is served one chapter at a
   time (D17).
2. Memory tier, then the durable tier, then prepare it here and now: fetch the chapter
   (scraping on a cache miss, cached in Mongo so the site is hit once per chapter) and wrap
   every word the reader has saved.
3. Claim the prefetch key for `chapterNumber + 1` and warm it in the background.
4. Return the **whole chapter**, each paragraph carrying its own real number.

The method *returns* its answer rather than pushing it, so the page can await one chapter
before placing the next — which is what resuming at a bookmark needs (D19). `GetReadingSession`
and `GetNovelProgress` work the same way; definitions, translations and vocabulary changes are
still pushed, because they are notifications.

**Progress.** `ReportProgress(novel, chapter, paragraph)` records the last paragraph the reader
actually had on screen. The page sends it once the reader has been still for two seconds, not
on every scroll event.

Chapter URLs are built as `book/{novelName}/chapter-{chapterNumber}` against the NovelFire base address, so `novelName` is the site's slug (e.g. `reverend-insanity`).

**Two rules keep this path from stampeding the source site (D17).** `ProcessAndReturnAsync` takes a `UserRequestGate` turn, so one reader is served one request at a time. And `SchedulePrefetch` claims a user/novel/chapter key *before* scheduling: every paragraph served asks for the next chapter to be warmed, so without that claim a 30-paragraph chapter queued 30 identical scrapes. The cache checks inside the prefetch cannot do that job — they all run before any of them writes. Prefetch must never take the gate; delaying the reader is exactly what D11 forbids.

### Storage layout

- Database `Novels`: **one collection per novel**, named by the novel slug. A chapter document is `{ _id, chapter: <int>, "1": "...", "2": "..." }` — paragraphs are `[BsonExtraElements]` on `Chapter`, keyed by the *stringified* paragraph number, hence `Dictionary<string, object>` and the cast in `TryGetParagraph`. Each collection carries a **unique index on `chapter`** (`chapter_unique`), ensured by `ChapterIndexes` the first time the novel is opened in a process; it also clears out any duplicates an earlier run stored. `TryGetExactlyOne()` keeps its `Single()` deliberately — it asserts the invariant the index guarantees (D17).
- Database `Users`, collection `ReadingProgress`: **one document per (user, novel)** —
  `{ user, novel, chapter, paragraph, updatedAt, title, rank, totalChapters, detailsCheckedAt }` —
  the bookmark plus the catalogue's details, refreshed daily (D22). **Both writers `$set` only
  their own fields**; a `ReplaceOne` here would wipe the details on every scroll. Note the two
  timestamps mean different things: `updatedAt` is when the reader last read, `detailsCheckedAt`
  is when the catalogue was last *asked* (stamped even when the lookup failed, so an outage
  cannot turn into a lookup per page load). The collection is unique on (user, novel), with a
  second index on (user, updatedAt); that shape is what makes the reader's novel list a `distinct`
  and a bookmark write a one-document upsert (D18). Documents in the old nested shape are
  migrated on start-up. **Mongo matches user names exactly while accounts match them
  case-insensitively**, so a legacy row for `Anton` is only reachable by an account registered
  as exactly `Anton`.
- SQLite (`novelreader-accounts.db`, gitignored, created on first run): the `accounts` table —
  credentials, the D31 translation settings, and the reader's AI (`assistant_service`,
  `assistant_model`, `assistant_key`). The key is Data-Protection-encrypted; the key ring lives
  in `novelreader-keys/` **beside the database** and must be kept with it, or every stored API
  key becomes unreadable (D35). **A deploy must carry over both** `novelreader-accounts.db`
  and `novelreader-keys/`. Losing the ring also signs everyone out, because it signs the
  auth cookie too.
- SQLite (`novelreader-monitoring.db`, beside the accounts database unless
  `MonitoringConnectionString` says otherwise): the admin panel's `auth_events`, `errors` and
  `response_times` (D45). Separate so that it can grow, be pruned or be lost without touching
  the accounts. The deploy script carries it over too.

### Frontend

Angular (D43): standalone components, signals, zoneless change detection, `OnPush`
everywhere, no NgModules. Sources in `WebApi/client/src/`; `ng build` emits content-hashed
files into `WebApi/wwwroot/`. **One HTML shell for both pages**: `LoginController` (`/Login`)
and `ReadingPageController` (`/ReadingPage`, signed-in readers only) both serve `index.html`
through `ClientShell`, which rewrites `<base href="/">` to the path base (`/reader` behind
nginx); the router then lazy-loads that page's component. Hashed files are cached for a year,
the shell is `no-cache` (`ClientAssets`) — this replaced D25's `/_v/{token}` prefix. Every URL
the page calls is built with `appUrl()` (`core/app-base.ts`) against that base; the hub is
mapped at `/signalr`. The SignalR client is now an npm import, bundled.

- `app/core/` — `ReaderConnection` (the hub calls and their wire shapes in `hub-models.ts`;
  pushed answers arrive as RxJS `Subject`s, the connection state as a signal), `ReaderState`
  (the reader's AI and translation settings as signals, shared by the box, the drawer and the
  review), `KeyboardRouter` (a stack of named key modes, innermost first, D6), `pointer.ts`.
- `app/login/login-page.ts` — the login screen. Posts JSON and acts on the answer in place.
- `app/reading/reading-page.ts` — the orchestrator (what `realTimeReader.ts` was): key modes,
  chapter loading and trimming, the touch affordances, the navigation screens. Paragraph **1**
  of a chapter is its title (`.data-chapter-title`) (D19). The column is an `@for` over
  `blocks`; every `<p>` carries `data-chapter` and `data-paragraph`, which is how the bookmark
  is read back off the page. Its markup is written **once** by the `ServerMarkup` directive
  (D3 — the server escapes it; Angular's sanitiser would strip `data-term`) and afterwards only
  patched in place by `underliner.ts` (D14), so a paragraph must never be re-rendered. Anything
  that changes `blocks` and then measures calls `#renderBlocks`, which runs `detectChanges()`
  so the DOM is there to measure. The framework-free helpers beside it — `progress-reporter`
  (the debounced bookmark), `reading-anchor` (D36), `scroller` (j/k and the scroll lock),
  `selection`, `underliner` — work on the DOM directly.
- `app/definition/definition-box.ts` — the definition panel: opened by the *request* showing
  `loading…`, filled in by the answer, after five seconds `still looking…` in dim when the
  connection is up and `connection lost` in red only when it is down (D33); senses paged with
  j/k, save/delete, the translation block. Also the translation **settings form** (`e`, or `t`
  before anything is configured) — email, then a filtered language list (D31). Its size is
  fixed in CSS and must not be measured from the content (D27); it is positioned after each
  render by an `afterRenderEffect`.
- `app/settings/settings-panel.ts` — the left drawer: `c`, or on touch the `>`/`<` button
  top-left. Sections are **projected** into it from `reading-page.html`, in order; a new
  option is a new section component there (D35). From the keyboard it is a grid (D36): j/k
  between `.settings-row`/`.settings-actions`, h/l along a row, Enter edits/presses, Escape
  leaves a field and then closes the drawer — worked out from those classes alone, so a new
  section gets the keys for free. A section that shows stored state reads the panel's
  `openings()` signal (inject `SettingsPanel`) to refresh when the drawer opens. Sections:
  `review-section` (first, AI readers only), `appearance-section` (per device, in
  `localStorage`, applied pre-paint by the inline script in `index.html`; chapter-title size
  independent, D38), `assistant-section`, `password-section` (D40).
- `app/navigation/navigation-menu.ts` — `NavigationMenu`, the `n` panel: a **stack of
  `MenuScreen`s**; activating a row can `push` another, Escape pops (clearing a filter first)
  and closes at the root. It handles no keys itself — the page binds `move`/`activate`/`back`
  through the router — except inside the filter field.
- `app/review/` — the vocabulary review (D44): `review-overlay` (the panel over the text),
  `review-section`, `speech-input` (Web Speech dictation).

Components declare **no `styles`** (CSP, above). The stylesheets are global, listed in order in
`angular.json`: `tui.css` defines the `--tui-*` tokens and the panels' scrollbars (D30) and must
come first; then `reader.css`, `navigation.css`, `definition-box.css`, `settings-panel.css`,
`review.css`, `login.css`. The reader's and the login screen's `body` rules are scoped by the
`reading-page` / `login-page` class each page puts on `<body>`. Component hosts are
`display: contents` (end of `reader.css`), so a host element never adds a box.

The top-right corner is a stack (`.reader-affordances`): the `n navigate` hint, the
`d definition` button a touch reader gets while text is selected (D26), and under that
`t translate`, only for a selection of **more than one word** (D32). Both ask for the
definition and the translation at once, which is why `AddSignalR` raises
`MaximumParallelInvocationsPerClient` and why a translation is matched to its box by surface
form. The bottom-left `reconnecting…` chip is the only sign of a dropped connection; the client
reconnects once a second on its own, hub calls wait for it, and a chapter load that a drop cut
short is retried when it returns — which is why `ChapterView` carries `failed` (D28).

Things to keep in mind when editing:

- **Anything that measures the reading column must wait for the web font.** The column is set
  in `Libre Calson Regular`; `waitForStableLayout()` awaits `document.fonts.ready` — and races
  the next frame against a timer, because `requestAnimationFrame` never fires in a background
  tab (D23).
- **The page holds at most four chapters** (D37). `trimChaptersAbove()` removes older ones when
  the reader settles and after each append, correcting the scroll offset. A font-size change
  keeps the *bottom* paragraph in place (`keepReadingPosition`), a rotation the *top* line
  (`ReadingAnchor`) (D36).
- **Nothing in the reading column may be wider than the column** (`overflow-wrap: anywhere`,
  D34). A paragraph that is only separator characters is drawn as a scene break.
- **Zoneless:** template state must be signals. A plain field changed from a SignalR callback
  or a timer will not re-render. Fields that only the code reads (the chapter loader's flags)
  stay plain.

### The vocabulary review (D44)

Offered only to a reader with their own AI (every hub method answers `ai-required` otherwise).
The **server decides everything**; the page shows exercises and sends back what the reader did.

- `ReviewScheduler` — Anki's SM-2 variant with Anki's default deck options (learning steps
  1 m/10 m, graduate to 1 d, easy 4 d, ease 2.5, hard ×1.2, easy bonus 1.3, lapse → relearn
  10 m and 1 d, minimum ease 1.3, 20 new words a day), no fuzz.
- `ReviewPlanner` — the queue (due learning, then due reviews, then today's new words; a
  learning card due within 20 min is taken early when nothing else is) and the **form**:
  `match` while new/learning or interval < 3 d, `recall` (own words) < 10 d, then `sentence`
  and — when a translation language is set — `translate` taking turns.
- `ReviewTutor` — the prompts: one-line glosses for matching (written once per word, kept on
  the card), the sentence to translate, and judging free answers as
  `correct`/`partial`/`wrong` → Good/Hard/Again. Uses `IAssistant.AskAsync`. An unreadable AI
  answer grades nothing; the exercise stays open.
- `ReviewService` — one open exercise per reader, **in memory** (a restart drops it, nothing
  is lost). A match's key stays on the server; pairs are checked one at a time.
- Cards: Mongo `Users.ReviewCards`, one per (user, term), unique index. A card is created
  lazily for each saved word and removed when its word is gone from the vocabulary.

### Saved words match in all their forms (D39)

`VocabularyEntry.BaseForm` comes from the dictionary at save time (`WordForms.BaseFromDictionary`):
an inflection note in the main sense, or below a non-noun main sense. Null when no dictionary
had an entry, in which case `WordForms.CandidateBasesOf` supplies every rule-valid base at match
time. `VocabularyForms` opens the vocabulary out into *form → saved term*, which is what
`ParagraphMarkupBuilder` matches against and what `ReturnVocabularyChanged` sends the page as
`forms`. **Changing what a paragraph marks up to means bumping `ParagraphMarkupBuilder.Version`**,
or the durable prepared-chapter cache keeps serving the old markup. Entries saved before D39 are
backfilled by `VocabularyBaseForms`, once per reader per process.

### Prototype seams to be aware of

The reader, the novel and the position all come from the server now; nothing about identity is
hardcoded on the client. A reader with no history starts at `reverend-insanity` chapter 1
(`RealTimeReaderHub.DefaultNovelName`).

All three navigation screens work: "Go to chapter" reuses `LoadChapter`, "Novels you've read"
is the reader's real `distinct` novel list, and "Search new novels" queries the source site
through `SearchNovels`. The chapter list is still only a window of `chapterWindow` chapters
either side of the current one, because nothing reports a novel's chapter count.

**`SearchNovelsRetriever` still returns two hard-coded rows** — it fetches the URL and
discards the response — so search shows the same two novels whatever is typed. The live
endpoint is `ajax/searchLive?keyword=…` (GET only; `total_chapter` is snake_case and needs a
`[JsonPropertyName]` to bind). Translation is live now (D31) — MyMemory, word-level, with the
reader's email and target language stored as two nullable columns on the SQLite account row.
**Translations are not cached** the way definitions are (D2); that is the obvious next step.

A reader can bring their own AI (D35): `AssistedDefinitionService` and `TranslationService`
ask it first, sending the paragraph as context, and fall back to the dictionaries/MyMemory
when it fails. `OpenAiCompatibleAssistant` (in `NovelReader.Dictionary`) speaks the OpenAI
chat-completions dialect to a fixed list of services (`AssistantServices`) — never a
user-supplied URL.

A signed-in reader can change their password from the settings drawer (D40): the current
password is required, the new one is typed once with an eye to show it, and other devices stay
signed in. There is still no
reset.

Five wrong passwords lock a username for five minutes, doubling per further lock up to a
day (D41). The count is `SignInThrottle`'s, in memory. Sign-in and the password change
each have their own, so an attacker at the sign-in form cannot stop the signed-in owner
changing the password. Counts are kept for names that have no account too, so a lock does not reveal whether a
name exists. Every attempt is counted *before* the password is checked, which is what holds
the limit against parallel guesses.

`ParagraphsRetriever` treats the source site's occasional `200`-with-no-paragraphs as a hard
failure; it wants a retry with backoff, and a way to tell a transient apart from the actual end
of a novel. It also recognises the site's "page moved" notice **by its wording** and throws
rather than letting it be cached as a two-paragraph chapter (D21) — if the site rewords that
notice, the junk-chapter behaviour comes back and `ParagraphsRetrieverTests` needs updating
with it.

The chapter menu lists only a window around the current chapter, but any number can be typed
into its filter to jump straight there (`MenuScreen.itemsFromFilter`).
