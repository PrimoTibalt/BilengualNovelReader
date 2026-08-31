# Vocabulary Reader — Task Breakdown

Feature set: look a word up without leaving the page, keep a per-user "encountered
previously" vocabulary, underline those words in the text, and prefetch chapters so none of
it stalls reading.

**Progress markers:** `[ ]` not started · `[~]` in progress · `[x]` done · `[!]` blocked or
needs a decision from the reader.

**Resuming after a context reset:** read `DECISIONS.md` first (it explains *why* the code
looks the way it does), then the phase table below, then `git status`/`git diff` to see
uncommitted work. Each task names the files it touches. Verification commands are at the
bottom.

---

## Status at a glance

| Phase | Scope | State |
|---|---|---|
| A | Docs: decisions log + task file | `[x]` done |
| B | Domain contracts | `[x]` done |
| C | Dictionary providers + definition cache | `[x]` done |
| D | Vocabulary storage (MongoDB) | `[x]` done |
| E | Paragraph markup builder | `[x]` done |
| F | Hub API surface | `[x]` done |
| G | Client: keyboard router + selection | `[x]` done |
| H | Client: definition box UI | `[x]` done, verified in a browser |
| I | Chapter prefetch + two-tier cache | `[x]` done, verified |
| J | PWA groundwork | `[ ]` deliberately deferred — "later" |
| K | Tests | `[x]` 44 passing + e2e verified |
| L | TUI navigation menu + translation stub | `[x]` done, verified in a browser |
| M | Fix the request storm + duplicate chapters | `[x]` done, verified against the live app |
| N | Accounts, resume-where-you-left-off, real novel list | `[x]` done, server verified; UI unverified in a browser |
| O | Novel search UI | `[x]` done; retriever still returns stub rows |
| P | Jump to any chapter by number | `[x]` done, verified against the live app |
| Q | Cached novel details, refreshed daily | `[x]` done, verified against the live catalogue |
| R | Browser verification + README | `[x]` done; two resume bugs found and fixed |

---

## Phase A — Documentation `[x]`

- [x] `DECISIONS.md` — decisions D1–D10 recorded, with the provider measurements that drove D1.
- [x] `TASKS.md` — this file.

---

## Phase B — Domain contracts `[x]`

Interfaces and value types only; no infrastructure. Lands in `NovelReader.Domain`, which
keeps its zero-package-reference rule.

- [x] **B1** `Vocabulary/VocabularyEntry.cs` — saved term: normalised key, original surface
      form, novel it came from, timestamp.
- [x] **B2** `Vocabulary/IVocabularyRepository.cs` — `Add`, `Remove`, `Contains`,
      `GetAllForUser`. All take a user name (D9).
- [x] **B3** `Definitions/WordDefinition.cs` + `DefinitionSense.cs` — a term with ordered
      senses (part of speech, text, optional example), plus source attribution (D1).
- [x] **B4** `Definitions/IDefinitionProvider.cs` — one lookup method; implemented once per
      provider.
- [x] **B5** `Definitions/IDefinitionCache.cs` — get/store, including negative caching (D2).
- [x] **B6** `Vocabulary/TermNormalizer.cs` — the normalisation rules from D4. Pure static
      logic, so it belongs in Domain and is directly testable.
- [x] **B7** `Reading/IPreparedChapterCache.cs` — the two-tier cache contract (D8).

## Phase C — Dictionary providers + cache `[x]`

New project `NovelReader.Dictionary`, matching the existing one-project-per-infrastructure-
concern layout with its own `ServiceCollectionExtension`.

- [x] **C1** Create project, add to solution, reference Domain, wire `IHttpClientFactory`.
- [x] **C2** `WiktionaryDefinitionProvider` — primary. Parses the REST shape; **strips the
      HTML fragments** Wiktionary returns (D1) down to plain text.
- [x] **C3** `FreeDictionaryDefinitionProvider` — fallback against dictionaryapi.dev. Short
      timeout; its 21 s worst case must never be on the critical path.
- [x] **C4** `FallbackDefinitionProvider` — tries primary, falls back on miss/error, and is
      what gets registered as `IDefinitionProvider`.
- [x] **C5** Per-provider timeouts + a polite `User-Agent` (Wikimedia asks for one).

## Phase D — Vocabulary + definition storage `[x]`

In `NovelReader.Data.Mongo`. Implementations stay `internal`, exposed only through the
existing `ServiceCollectionExtension`.

- [x] **D1t** `VocabularyMongoRepository` — collection `Vocabulary` in the `Users` database,
      one document per user holding their terms.
- [x] **D2t** `DefinitionMongoCache` — global `Definitions` collection (D2), negative entries
      included.
- [x] **D3t** Index on the vocabulary lookup path; the markup builder hits it once per
      paragraph batch, so it must not table-scan.

## Phase E — Paragraph markup `[x]`

In `NovelReader.Domain`, since it is pure logic over text.

- [x] **E1** `Reading/ParagraphMarkupBuilder.cs` — escape-first, then wrap known terms
      (D3). Emits `<span class="known-word" data-term="…">`.
- [x] **E2** Longest-match phrase scanning, capped at 4 tokens (D4).
- [x] **E3** **Escaping test is mandatory** — scraped text containing `<script>` or a
      quote must come out inert. This is the one place untrusted text becomes markup.

## Phase F — Hub API `[x]`

`WebApi/RealTimeReaderHub.cs`.

- [x] **F1** `GetNextParagraph` now returns marked-up HTML instead of plain text.
- [x] **F2** `GetDefinition(userName, term)` → senses + whether the term is already saved
      (the box needs to know whether to offer save or delete).
- [x] **F3** `SaveWord(userName, term, novelName)`.
- [x] **F4** `DeleteWord(userName, term)`.
- [x] **F5** Both mutations invalidate that user's prepared-chapter cache (D8), since
      underlines are baked into cached markup.

## Phase G — Client keyboard + selection `[x]`

`WebApi/src/`.

- [x] **G1** `input/keyboardRouter.ts` — the mode stack from D6.
- [x] **G2** Port `vim-motioned` to register as the `reading` mode's `j`/`k` binding rather
      than listening to the document itself.
- [x] **G3** Selection capture — `window.getSelection()`, trimmed, multi-word allowed.
- [x] **G4** `d` on a selection requests a definition; `s` saves. `s` must work both from a
      raw selection and from an open box.
- [x] **G5** Click handler on `.known-word` opens the box for that term.

## Phase H — Definition box UI `[x]` (unverified in a browser)

- [x] **H1** `ui/definitionBox.ts` — build, position near the word, tear down.
- [x] **H2** CSS comic bubble: square panel + triangular tail pointing at the word, pure CSS
      borders (D5).
- [x] **H3** Height measured from the first sense, then locked (D7) — `[!]` interpretation
      still unconfirmed, see the open question.
- [x] **H4** `j`/`k` page through senses; a counter shows position (`2/5`).
- [x] **H5** Scroll lock while open, released on close.
- [x] **H6** `Esc` closes. `t` reaches the translation seam and reports "not configured"
      (D10). `d` deletes when the term is saved.
- [x] **H7** TUI-style hint bar along the bottom of the box showing live key bindings.
- [x] **H8** Tail must flip above/below the word near a viewport edge.

## Phase I — Chapter prefetch `[x]`

- [x] **I1** `PreparedChapterMongoCache` — `_id` of `{user}/{novel}/chapter{N}` exactly as
      specified.
- [x] **I2** In-memory tier via `IMemoryCache`, with a bounded size.
- [x] **I3** On first open of a chapter: prepare N and N+1, send N, store N+1.
- [x] **I4** On reaching a cached chapter: promote it to memory, then prepare and store N+2.
- [x] **I5** Prefetch runs in the background — it must never delay the paragraph the reader
      is waiting for.
- [x] **I6** Invalidate on vocabulary change (pairs with F5).

## Phase J — PWA groundwork `[ ]`

Deliberately last; the reader said "later". Only the parts that are cheap to get right now.

- [ ] **J1** `manifest.webmanifest` — standalone display, so the browser chrome stops
      meddling.
- [ ] **J2** Icons.
- [ ] **J3** `[!]` Service worker — decide the caching story. Offline reading interacts with
      the prefetch design in Phase I and should not be bolted on carelessly.

## Phase K — Tests `[x]`

There is no test project in the solution yet; this adds the first one.

- [x] **K1** `NovelReader.Tests` (xUnit), added to the solution.
- [x] **K2** Markup escaping (E3) — the security-relevant one.
- [x] **K3** Term normalisation (B6) across case, punctuation, possessives.
- [x] **K4** Longest-match phrase selection.
- [x] **K5** Provider fallback: primary miss → secondary consulted; primary error → secondary
      consulted.

---

## Phase L — TUI navigation menu + translation stub `[x]`

Brings the navigation menu up to the definition box's look, and makes `t` a real round trip.

- [x] **L1** `wwwroot/tui.css` — the shared `--tui-*` palette. `definition-box.css` now
      aliases onto it; its values are unchanged, so the box looks exactly as it did.
- [x] **L2** `NavigationMenu` replaces `attachFocusToList` / `generateNextFocusListOnSelected`:
      a screen stack rendering the same panel chrome — titled header with a breadcrumb, a
      position counter, a filter row, a hint bar (D16).
- [x] **L3** Menu driven by a `navigation` router mode. Opens on `n`, `j`/`k`/arrows move,
      `g`/`G` jump to the ends, Enter selects, `/` (and the old `?`) filters, Escape goes back
      and closes at the root. Scrolling is locked while it is open.
- [x] **L4** Filter matches on substring and underlines the hit in the row.
- [x] **L5** Chapter screen — a window of ±`chapterWindow` chapters, opening on the chapter
      being read. Selecting one restarts the column there via `GetNextParagraph`.
- [x] **L6** Library and search screens render a named empty state rather than a dead row;
      both are waiting on hub methods.
- [x] **L7** Hub `Translate` → `ReturnTranslation` with `TranslationResponse.Stub` (D10).
- [x] **L8** The box shows "translating…" while it waits, then the reply plus a
      "placeholder" note, re-measuring and re-placing itself so the new block is visible.
      The translation survives paging senses with j/k.

---

## What has been verified, and how

Run against a **synthetic novel seeded straight into MongoDB**, so nothing here touched the
live novel site.

| Behaviour | Result |
|---|---|
| Save single word `"Ephemeral,"` | normalised and stored as `ephemeral` |
| Save phrase `give up` | stored as a phrase |
| Markup, case | `The Ephemeral Sect` → `<span … data-term="ephemeral">Ephemeral</span>` |
| Markup, possessive | `ephemeral's` wrapped whole, apostrophe escaped to `&#39;` |
| Markup, longest match | `give up` wrapped as one span, not as bare `give` |
| **Escaping of scraped text** | `<b>an old text</b> & smiling` served as `&lt;b&gt;…&amp;` — inert |
| Definition lookup, cold | 163 ms, 5 senses, source Wiktionary |
| Definition lookup, cached | **2 ms** |
| Unknown word, cold | 6216 ms (Wiktionary 404 → fallback timeout), `found=false` |
| Unknown word, cached | **3 ms** — negative caching earning its keep (D2) |
| Prefetch | reading ch2 left `testreader/test-novel/chapter3` in the durable tier |
| Vocabulary index | `user_term_unique` created, unique |
| Delete word | vocabulary entry removed, `ReturnVocabularyChanged` sent |

**Not yet verified:** the definition box has never been rendered in a browser. Its geometry
— tail placement, the locked height, edge flipping — is written but unseen. That is the
first thing to check.

## Notes for the next session

- `NextParagraphProcessor` now takes `userName` and serves **marked-up HTML**, not plain
  text. The client must render paragraphs with `innerHTML` (the server escapes; see D3).
- New hub methods: `GetDefinition`, `SaveWord`, `DeleteWord`. Client callbacks:
  `ReturnDefinition`, `ReturnVocabularyChanged`.
- Prefetch is wired through `IBackgroundWorkScheduler` so it never blocks a paragraph.
  Implementation logs and swallows failures.
- `I5` is coded but has **not** been exercised against a live novel yet.

## Phase L, verified in Chrome against the running app

Run on a scratch copy of the repo on port 5262, against the live novel site and MongoDB.

| Behaviour | Result |
|---|---|
| `n` opens the menu; affordance hides | panel renders as the definition box does |
| Enter into "Go to chapter" | breadcrumb `navigation / chapters`, counter `13/25` |
| Chapter list opens on the current chapter | Chapter 200 selected, marked `current` |
| `/` then `20` | filtered to `1/10`, the `20` underlined in each row |
| Select Chapter 205 | column cleared and reloaded; menu reports `reading 205` |
| Library screen | "Not wired up yet — no hub method lists the novels you have read." |
| Escape | pops to the parent screen, closes at the root, affordance returns |
| `d` on a selection, then `t` | "translating…" → the server's stub string + placeholder note |
| `j` after translating | sense `2/4`, translation still shown |

**A note on the automation:** the browser tool's `slash` keyname does not produce
`event.key === "/"`. The binding is correct — a dispatched `/` keydown focuses the filter.

---

## Phase M — The request storm and duplicate chapters `[x]`

Both bugs found while verifying Phase L, and both traced to one cause: every paragraph served
queued a prefetch of the next chapter, so a chapter's worth of paragraphs queued that many
identical scrapes. Their cache checks all ran before any of them stored anything (D17).

- [x] **M1** `UserRequestGate` — serialises `ProcessAndReturnAsync` per user name; different
      readers never wait on each other. Registered as a singleton.
- [x] **M2** In-flight prefetch de-duplication in `NextParagraphProcessor`: the
      user/novel/chapter key is claimed *before* scheduling and released in a `finally`.
- [x] **M3** `ChapterIndexes` — a unique index on `chapter` per novel collection, ensured
      once per novel per process, preceded by a clean-up of duplicates an earlier run stored.
- [x] **M4** `CollectionOfChapters.InsertOneAsync` treats a duplicate-key error as "another
      writer stored it first" and returns their copy.
- [x] **M5** Ten tests: five for the gate, five for the prefetch/scrape counts.

### Verified against the live app

Driven over SignalR with a Node client reproducing the reading page's exact load pattern.

| Measure | Before | After |
|---|---|---|
| `429 Too Many Requests` in a session | 64 | **0** |
| `Sequence contains more than one element` | 85 | **0** |
| Duplicate chapter documents | 201 ×28, 206 ×25, 301 ×23 | **none** |
| Site requests to serve 25 paragraphs | one prefetch per paragraph | **1 chapter + 1 prefetch** |
| Deliberate duplicate insert | accepted | **rejected, `E11000`** |

The migration removed **73** duplicate documents on first start; the collection now holds one
document per chapter (12 docs, 12 distinct chapters).

**The tests have teeth:** disabling the de-duplication guard fails two of them.

### Still open — the site's empty responses

Separate from the above and not fixed: novelfire.net intermittently answers `200` with a page
containing no paragraphs, and `ParagraphsRetriever` turns that into
`No paragraphs found at '…'`, failing the reader's request. Seen on chapters 203, 311 and 320
during verification. Wants a retry with backoff; chapter 320 may simply be past the end of the
novel, which is worth distinguishing from a transient.

---

## Phase N — Accounts, bookmarks and a real novel list `[x]`

- [x] **N1** SQLite accounts (`NovelReader.Data.Sqlite`), PBKDF2-HMAC-SHA256 hashing,
      usernames unique and case-insensitive via `COLLATE NOCASE` (D20).
- [x] **N2** Cookie authentication. `/ReadingPage` requires a reader; `/Login` and `/auth/*`
      do not. No hub method takes a user name any more.
- [x] **N3** TUI login screen — one panel, two fields, sign-in/sign-up tabs, errors shown in
      place rather than by reloading.
- [x] **N4** Reading progress rewritten as one document per (user, novel), with a start-up
      migration off the old nested shape (D18).
- [x] **N5** `LoadChapter` returns a whole chapter and *returns* it, so the page can sequence
      loads. Paragraphs now carry their real numbers instead of `number + 1` (D19).
- [x] **N6** Resume: open at the stored bookmark with that paragraph at the bottom of the
      viewport, loading the previous chapter too when the bookmark is near the top of its own.
- [x] **N7** `ProgressReporter` — one bookmark request once the reader has been still for two
      seconds, plus an immediate flush when the page is hidden.
- [x] **N8** The navigation menu's library is the reader's real novel list, from `distinct`
      on the progress collection; picking one resumes *that* novel's bookmark. Sign-out added.
- [x] **N9** 26 tests: password hashing, account rules, and the SQLite uniqueness guarantee.

### Verified against the running app

Auth over HTTP, and the reading API over SignalR with a real cookie.

| Behaviour | Result |
|---|---|
| `/` and `/ReadingPage` while signed out | 302 to `/Login` |
| Hub negotiate while signed out | 401 |
| Sign-up: short password / bad username | 400, each with its own reason |
| Sign-up: duplicate, and duplicate in another case | 400 "already taken" both times |
| Sign-in: wrong password vs unknown user | identical 401 and identical wording |
| Sign-in with different casing | succeeds, returns the registered spelling |
| Brand-new reader | reverend-insanity ch1, no history, empty novel list |
| `LoadChapter` | 90 paragraphs in one call, real paragraph numbers |
| Report a bookmark, re-open the session | resumes that chapter and paragraph |
| Two novels read | `distinct` list of both, most recent first |
| Open a novel from the library | resumes that novel's own bookmark |
| Legacy progress document | migrated on start-up; `'200'` string became `200` |

The bookmark timing was checked against the compiled `ProgressReporter` with a stub DOM:
**25 scroll events produce exactly one request, ~2s after the last one**; an unchanged
position is not resent; a restored bookmark is not echoed back; hiding the page flushes at
once.

### Not verified at the time — now settled in Phase R

The Chrome extension was unavailable during this phase, so the login screen, the resumed
paragraph's placement, the one-or-two-chapter load and the library menu were all unverified
in a browser. Phase R checked every one of them, and **found two real bugs in the resume path**
that the headless checks could not have seen.

### A wrinkle worth knowing

Mongo matches user names exactly; the account store matches them case-insensitively. The
migrated legacy row belongs to `Anton`, so it is only reachable by an account registered as
exactly `Anton` — signing up as `anton` starts fresh. Registering the capitalised name adopts
the old bookmark.

---

## Phase O — Novel search in the menu `[x]`

Built on the `ISearchNovelsRetriever` API added outside these phases.

- [x] **O1** Hub `SearchNovels(query)`. Queries under two characters are not sent on.
      Deliberately outside the reading gate — a search must not queue behind a chapter load.
- [x] **O2** The search screen's filter field is a *server query*, not a local filter:
      `MenuScreen.onQuery` suppresses local filtering and fires **1.5 s after the last
      keystroke**. Pending queries are cancelled when the screen is left.
- [x] **O3** A rising generation counter drops a slow answer to an older query, so results
      cannot land out of order.
- [x] **O4** Rows read `<name>  <rank> <chapters>ch` on one line. Rank uses real English
      ordinals (1st/2nd/3rd), not a bare "th".
- [x] **O5** A name too long for the row fades out over its last two characters
      (`mask-image` gradient) instead of being clipped, so the rank and length keep their
      place. The class is applied only when the label actually overflows — measured, because
      a mask on a short name would fade empty space.
- [x] **O6** Picking a result opens that novel at its own bookmark.

### Verified

Over SignalR against the running app: `SearchNovels` answers with title/slug/rank/chapters;
one- and zero-character queries are refused before any request is made; the server reaches
`ajax/searchLive` and gets a 200.

Against the compiled `NavigationMenu` with a stub DOM: typing "shadow" letter by letter sends
**exactly one query, 1502 ms after the last keystroke**; the filter text survives results
landing; a short name is not faded and an over-long one is; rank and length render in their
own column; and leaving the screen cancels a query still pending.

### The endpoint, for when the parsing lands

`SearchNovelsRetriever` currently fetches the URL and then **discards the response**, returning
two hard-coded rows, so the UI shows Shadow Slave and Reverend Insanity whatever is typed. The
real endpoint is:

```
GET https://novelfire.net/ajax/searchLive?keyword=<url-encoded>
{"data":[{"title":"Shadow Slave","slug":"shadow-slave","rank":1,"total_chapter":3168,"image":"…"}]}
```

Note `total_chapter` is snake_case: `NovelData.TotalChapter` will not bind to it without
`[JsonPropertyName]` or a snake-case naming policy. `q`, `inputContent`, `search` and `title`
all return `{"data":null}` — only `keyword` works, and the route is GET-only.

---

## Phase P — Any chapter by number `[x]`

- [x] **P1** `MenuScreen.itemsFromFilter` — rows built from the filter text, shown after the
      matches, so a screen can offer what its list does not hold (D21).
- [x] **P2** The chapter screen offers any valid number outside its window, marked
      "not in the list — try it". Digits only: `1e3`, `0x10`, `12.0` and `-5` offer nothing.
- [x] **P3** `parseChapterNumber` moved to `src/reading/chapterNumber.ts` so it can be
      checked on its own.
- [x] **P4** `ParagraphsRetriever` recognises the source site's "page moved" notice and
      throws, so a miss is never cached as a chapter.
- [x] **P5** Seven tests for the retriever, covering the notice, a short-but-real chapter, a
      real chapter containing the marker words, a missing content div, and a failed request.

### Verified

Against the compiled menu with a stub DOM: the window lists 25 chapters; typing `500` offers
exactly one row and selecting it jumps there; `205` is not offered twice; `20` still matches
the window with the exact chapter offered after it; nonsense offers nothing; `999999` is still
attempted.

Against the running app: chapter 999999 answers `found: false` and **stores nothing**, a real
chapter outside the window still loads, and the connection survives the miss.

### The bug this uncovered

Before P4, asking for chapter 999999 left **two** junk chapters in Mongo — 999999 and, via the
prefetch, 1000000 — each holding the site's "page moved" notice, cached as though it were
content. Both were removed from the development database. Anyone whose database predates this
fix may have similar rows; they are recognisable as chapters with two or three paragraphs
about pages having moved.

---

## Phase Q — Novel details cached beside the bookmark `[x]`

- [x] **Q1** The (user, novel) row carries `title`, `rank`, `totalChapters` and a separate
      `detailsCheckedAt` timestamp (D22).
- [x] **Q2** `NovelLibraryService` returns the stored library at once and schedules a
      background refresh for anything older than 24 hours, or never checked.
- [x] **Q3** Refresh goes through the search endpoint (JSON) rather than the novel's page
      (HTML), searching by stored title or by de-hyphenated slug, and takes only the result
      whose slug matches.
- [x] **Q4** The timestamp records the *attempt*: a novel the catalogue cannot find, and a
      catalogue that is down, are both retried tomorrow rather than on the next page load.
- [x] **Q5** `SaveAsync` and `SaveNovelDetailsAsync` both `$set` only their own fields — the
      previous `ReplaceOneAsync` would have wiped the details on every bookmark move.
- [x] **Q6** The library menu shows `<title>  <rank> <chapters>ch`, the same one-line row as
      search, falling back to the slug for a novel not looked up yet.
- [x] **Q7** 24 tests: staleness boundaries, scheduling, de-duplication, slug matching, the
      outage path, search-path building, and the retriever's JSON binding.

### Verified against the live catalogue

Three novels with real reading progress, none with details:

| Step | Result |
|---|---|
| First session load | 3 novels, all `title/rank/totalChapters` null — returned without waiting |
| ~9s later | Shadow Slave 1st/3168ch, My Living Shadow System 27th/1194ch, Reverend Insanity 3rd/2334ch |
| 4 further session loads | **no new catalogue requests** — 3 lookups total, then silence |
| Moving a bookmark | details survive; the bookmark itself moves |

### The bug this uncovered

`SearchNovelsRetriever` bound `TotalChapter` to a field the catalogue sends as
`total_chapter`. `PropertyNameCaseInsensitive` does not bridge an underscore, so **every
chapter count would have been null** — the feature would have stored and shown nothing for it.
Fixed with `JsonNamingPolicy.SnakeCaseLower`, and covered by
`SearchNovelsRetrieverTests`. The same method dereferenced `Data` without a null check, and
the endpoint answers `{"data":null}` when it dislikes a request; that is now an empty list.

---

## Phase R — Browser verification and the README `[x]`

Everything earlier phases could only check headlessly, checked in Chrome against the running
app.

| Behaviour | Result |
|---|---|
| Login screen renders; `/ReadingPage` redirects to it signed out | as designed |
| Sign-up mode switch | caption, button and the 8-character hint all follow |
| Sign in → reading page | resumes the stored novel |
| Bookmark placement | paragraph's bottom exactly on the viewport bottom (`offBy: 0`) |
| Bookmark near the top of a chapter | loads the previous chapter too; 7080 px of it above |
| Library menu | titles, `1st 3168ch`, `27th 1194ch`; long name faded |
| Search menu | 6 live results, ordinals to `1072nd`, fade on the long titles |
| Definition box | real Wiktionary sense, translation stub, tail on the word |

### Two bugs found, both in the resume path (D23)

1. **The web font moved the column after the scroll.** The bookmarked paragraph landed
   **160 px below the fold**, while re-running the identical scroll a moment later placed it
   exactly. `openNovelAt` now awaits `document.fonts.ready` first.
2. **The first fix hung in a background tab.** It waited a frame with
   `requestAnimationFrame`, which a hidden tab never fires — so the scroll never happened at
   all and `loadingChapter` stayed true, wedging the chapter loader. The frame wait is now
   raced against a 100 ms timer.

Neither was visible headlessly: the arithmetic of "put this paragraph at the bottom" was
correct throughout. What could not be seen was that it ran against a layout about to change,
in a tab that was never going to paint.

### README

`README.md` with four screenshots under `docs/screenshots/`, referenced by relative path so
they render on GitHub without cloning. The screenshots use a **synthetic novel written for
them** and a throwaway account, both removed afterwards — no scraped chapter text is committed
to the repository.

---

## Open questions for the reader

Answers change the work; none of them block the phases above, which proceed on the stated
assumption.

1. **Definition box height (D7).** Locked to the first sense, or resized per sense? Built as
   locked.
2. **Stemming (D4).** Should saving `run` underline `running`? Currently no — exact
   normalised match only. Proper lemmatisation needs a dependency.
3. **Translation (D10).** Which language pair, and word or surrounding sentence? The round
   trip now works end to end; only the answer is a stub, so this is the one thing blocking a
   real provider.
6. **Chapter count.** Nothing reports how many chapters a novel has, so the chapter menu is a
   window around the current one and "load the next chapter" discovers the end by trying it.
7. **Sessions.** The auth cookie lasts 30 days and slides. There is no password change, no
   reset, and no rate limit on sign-in attempts.
4. **`d` is overloaded.** It defines a selection in reading mode and deletes a saved word in
   definition mode. That is what was asked for, and the modes keep it unambiguous, but it is
   worth a second look — a mis-timed `d` deletes vocabulary.
5. **Vocabulary scope.** Is a saved word global to the reader, or per novel? Stored with its
   novel either way, but currently matched globally.

---

## Verification

```bash
dotnet build NovelReader.sln          # includes the TypeScript compile
cd WebApi && npm run typecheck        # client types only
dotnet test                           # once Phase K exists
podman start novelreader-mongo        # database must be up to run
dotnet run --project WebApi           # http://localhost:5261/ReadingPage
```
