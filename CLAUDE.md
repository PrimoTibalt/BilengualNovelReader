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

`run.sh` is the one-command path: it handles the three prerequisites below (podman
Mongo, libman restore, and the build) and then runs the app in the foreground. The
Mongo container is left running after Ctrl-C so cached chapters survive a restart.

The TypeScript client is compiled by `dotnet build` (see the `CompileClientTypeScript` target in `WebApi/NovelReader.csproj`), so it needs no separate step. To iterate on the frontend alone, from `WebApi/`:

```bash
npm run watch      # recompile on save
npm run typecheck  # type-check without emitting
dotnet build -p:SkipClientBuild=true   # skip tsc, reuse the JS already in wwwroot
```

Tests: `dotnet test NovelReader.Tests` (xunit). No lint/format config.

### Three prerequisites the build does not cover

- **ASP.NET Core runtime + targeting pack.** The projects target `net10.0` and `WebApi` uses `Microsoft.NET.Sdk.Web`, which needs the ASP.NET Core shared framework — it is *not* part of the base .NET SDK on Arch. Without the targeting pack the build fails with `NETSDK1226: Prune Package data not found ... Microsoft.AspNetCore.App`; without the runtime the app fails at launch with `No frameworks were found`. Install both:
  ```bash
  sudo pacman -S aspnet-runtime aspnet-targeting-pack
  ```
  `-p:AllowMissingPrunePackageData=true` will get a build through without the targeting pack, but the app still will not run — don't bake that flag into the project files.

- **SignalR client JS is not in the repo.** `wwwroot/lib/` is gitignored, so `signalr.min.js` must be restored after a fresh clone or the page loads with `signalR` undefined. The CLI installs to `~/.dotnet/tools`, which is not on `PATH` by default:
  ```bash
  dotnet tool install -g Microsoft.Web.LibraryManager.Cli
  cd WebApi && ~/.dotnet/tools/libman restore
  ```
  Destinations in `libman.json` are relative to the **project root**, not `wwwroot` — so the destination must read `wwwroot/lib/microsoft-signalr/`. A bare `lib/…` restores to `WebApi/lib/` where the served page cannot see it.

- **MongoDB must be reachable** at the `DefaultConnectionString` in `WebApi/appsettings.json` (`mongodb://admin:password@127.0.0.1:27017`). It is not in the Arch repos; a rootless container matching those credentials:
  ```bash
  podman run -d --name novelreader-mongo -p 27017:27017 \
    -e MONGO_INITDB_ROOT_USERNAME=admin -e MONGO_INITDB_ROOT_PASSWORD=password \
    -v novelreader-mongo-data:/data/db docker.io/library/mongo:8
  ```
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

- **The CSP allows scripts from this origin only.** An inline `<script>` in `index.html` or
  `login.html` is allowed by a hash computed from the file at start-up, so editing one needs
  a restart, nothing more. An inline `style="…"` attribute or a script from a CDN will be
  blocked. Set styles through `element.style` or a stylesheet.
- **A request from another origin is refused with 403**: the hub, and any POST/PUT/DELETE.
  A new page that posts from somewhere other than this origin will not work.
- **Novel names are checked against `NovelSlug`** in the hub. A new hub method that takes a
  novel name must check it too.
- `AllowedHosts` lists the host names the app answers to.

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

### Frontend

TypeScript, no framework and no bundler. Sources live in `WebApi/src/`; `tsc` emits browser-native ES2022 modules straight into `wwwroot/`, mirroring the directory layout, and `UseStaticFiles` serves them. `ReadingPageController` serves `index.html` at `/ReadingPage` (signed-in readers only); `LoginController` serves `login.html` at `/Login`; the hub is mapped at `/signalr`.

- `src/realTimeReader.ts` — entry point: wires the connection, the keyboard modes, the definition box and the navigation menu's screens. Paragraph **1** of a chapter is its title (`.data-chapter-title`) — paragraphs now arrive with their real numbers (D19). Every rendered `<p>` carries `data-chapter` and `data-paragraph`, which is how the bookmark is read back off the page.
- `src/login.ts` — the login screen. Posts JSON and acts on the answer in place, so a rejected password does not cost a reload.
- `src/input/keyboardRouter.ts` — a stack of named key modes, innermost first (D6). Opening the definition box or the menu pushes a mode; closing pops it. Text fields are left alone, so a focused filter input keeps its own keys.
- `src/reading/` — `connection.ts` (the hub calls and their wire shapes), `progressReporter.ts` (the debounced bookmark), `readingAnchor.ts` (records the top-of-screen paragraph while the column width is unchanged and restores it when the width changes, e.g. a rotation, even one made while the page was hidden (D36)), `scroller.ts` (j/k smooth scroll and the scroll lock), `selection.ts`, `underliner.ts`.
- `src/ui/definitionBox.ts` — the definition panel: opened by the *request* showing `loading…`, filled in by the answer, after five seconds of silence, `still looking…` in dim when the connection is up and `connection lost` in red only when it is genuinely down (D33); senses paged with j/k, save/delete, and the translation block filled in by `t`. It is also
  the translation **settings form** (`e`, or `t` before anything is configured): email then a
  filtered language list, using the navigation menu's keys — type to narrow, Enter forward,
  Escape back (D31). The form is the one thing exempt from the fixed body height. Its size is fixed in CSS — three lines tall and `min(46ch, calc(100% - 24px))` wide — and must not be measured from the content or it shrinks against the right edge of the screen (D27).
- `src/ui/settingsPanel.ts` — the left settings drawer: `c`, or on touch the `>`/`<` button
  top-left (on a keyboard that corner is a decorative `c settings` hint, like `n navigate`).
  No animation. A scrolling stack of `SettingsSection`s; a new option is a new section in the
  list in `realTimeReader.ts` (D35). From the keyboard it is a grid (D36): j/k between rows
  (`.settings-row`, `.settings-actions`), h/l along a row (or the slider's value), Enter
  edits/presses, Escape leaves a field and then closes the drawer. New sections get this free
  as long as they build rows with `settingsRow`/`settingsActions`. `appearanceSection.ts` (font size, chapter-title size (independent, D38), colours — per device, in
  `localStorage`, applied pre-paint by an inline script in `index.html`),
  `assistantSection.ts` (the reader's AI: service, model, key) and `passwordSection.ts` (D40).
- `src/interactive-select/module.ts` — `NavigationMenu`, the TUI panel opened with `n`. It is a **stack of `MenuScreen`s**, not one list: activating a row can `push` another screen, Escape pops (clearing an active filter first) and closes at the root. It renders the panel itself and handles no keys — the caller binds `move`/`activate`/`back` through the router — except inside the filter field, which owns its keystrokes while focused.

The top-right corner is a stack (`.reader-affordances`): the `n navigate` hint, the
`d definition` button a touch reader gets while text is selected — the phone's `d` key (D26) —
and under that `t translate`, which appears only for a selection of **more than one word**
(D32). `t` does the same from a keyboard, in reading mode. Both ask for the definition and the
translation at once and render whichever lands first, which is why `AddSignalR` raises
`MaximumParallelInvocationsPerClient` (the default of 1 queued the translation behind the
definition) and why a translation is matched to its box by surface form rather than term. A settled selection no longer opens anything by itself. The bottom-left `reconnecting…`
chip is the only sign of a dropped connection; the client reconnects once a second on its own,
hub calls wait for it rather than failing, and a chapter load that a drop cut short is retried
when it returns — which is why `ChapterView` carries `failed` as well as `found` (D28).

All three panels share one palette: `wwwroot/tui.css` defines the `--tui-*` tokens and must be linked before `interactive-select/styles.css`, `definition-box.css`, `settings-panel.css` and `login.css`, which draw from it. Restyling the chrome means editing the tokens, not the panels. It also draws the panels' **scrollbars**, which are the one piece of shared chrome the panels do not style themselves: the two engines have to be told the same width in different languages, and the split is delicate enough that it lives in one place (D30).

Three things to keep in mind when editing:

- **Anything that measures the reading column must wait for the web font.** The column is set
  in `Libre Calson Regular`; until it loads, paragraphs sit at the fallback font's metrics and
  then move. `waitForStableLayout()` awaits `document.fonts.ready` — and races the following
  frame against a timer, because `requestAnimationFrame` never fires in a background tab and
  waiting on it alone wedges the open (D23).

- **The page holds at most four chapters** (D37): the one being read, two above, one below.
  `trimChaptersAbove()` removes older ones when the reader settles and after each append,
  correcting the scroll offset so nothing visible moves. A font-size change keeps the
  *bottom* paragraph in place (`keepReadingPosition`), and a rotation keeps the *top* line
  (`ReadingAnchor`). Both are deliberate (D36).

- **Nothing in the reading column may be wider than the column.** Paragraphs carry
  `overflow-wrap: anywhere` — a chapter whose scene separator is 109 hyphens grew the phone's
  *layout viewport* to 1472px and let the page pan sideways, taking the fixed corner buttons
  off screen (D34). A paragraph that is only separator characters is drawn as a scene break.
- **The emitted `.js` and `.js.map` under `wwwroot/` are build output and are gitignored.** Edit the `.ts` in `src/`, never the generated file — a `dotnet build` overwrites it.
- **The SignalR client is a global, not a module.** It is loaded by a classic `<script>` tag from the libman-restored `lib/`, so `realTimeReader.ts` types it with `import type * as SignalR` plus `declare const signalR`. That import is erased at compile time; adding a value import of `@microsoft/signalr` would emit a bare specifier the browser cannot resolve without a bundler.

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
