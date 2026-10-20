# Decisions Log

Design, library, and architecture decisions taken while building the vocabulary-reader
features. Newest entries at the bottom. Each entry records *why*, so a later reader can
tell a deliberate choice from an accident.

Status values: **Adopted** · **Provisional** (works, but rests on an assumption worth
confirming) · **Deferred** · **Superseded by Dnn**.

---

## D1 — Dictionary source: Wiktionary REST primary, dictionaryapi.dev fallback

**Status:** Adopted · 2026-08-31

**Context.** Cambridge's API is paid. The two credible free options were measured from this
machine before choosing:

| Provider | Auth | Latency | Unknown word | Definition format |
|---|---|---|---|---|
| `en.wiktionary.org/api/rest_v1` | none (User-Agent required) | 0.7–3.7 s | clean `404` | HTML fragments |
| `api.dictionaryapi.dev` | none | ~21 s, and `522` on every request for a stretch | `522`, not `404` | clean plain text |

During testing dictionaryapi.dev degraded to Cloudflare `522` for *every* request —
including words it had served minutes earlier — then recovered but stayed at ~21 s. It is
community-run with no SLA. Wiktionary is Wikimedia-hosted infrastructure.

**Decision.** `IDefinitionProvider` is an interface with two implementations. Wiktionary is
primary; dictionaryapi.dev is tried only when Wiktionary misses or errors. Its cleaner
plain-text output is worth keeping as a second opinion, but never on the critical path.

**Consequences.**
- Wiktionary definitions arrive as HTML fragments (`<a>`, `<span>` wrappers) and must be
  stripped to plain text server-side. See D3.
- A per-provider timeout keeps a slow provider from stalling a lookup; the reading flow
  must never block on a dictionary call.
- Wiktionary text is CC BY-SA 3.0, so the definition box names its source. This is both a
  licence obligation and useful to the reader.

---

## D2 — Definitions cached in MongoDB, keyed by normalised term

**Status:** Adopted · 2026-08-31

**Context.** Lookups are slow (0.7 s at best, 21 s at worst) and both providers are free
services that deserve to be treated politely. The same words recur constantly in one novel.

**Decision.** Successful lookups are written to a `Definitions` collection keyed by the
normalised term (see D4), with the provider name and fetch timestamp. Cache is consulted
before any network call. Negative results (word genuinely not found) are cached too, with a
shorter lifetime, so a typo or a proper noun is not re-fetched on every encounter.

**Consequences.** The cache is global rather than per-user — a definition of "ephemeral" is
the same for everyone. Only the *vocabulary* (which words a user has saved) is per-user.

---

## D3 — Paragraph markup is built server-side; the client sets `innerHTML`

**Status:** Adopted · 2026-08-31

**Context.** Requirement: saved words must be underlined in the text, and the server should
hand the client markup that is already tagged. Today the client uses `innerText`, which
cannot carry tags.

**Decision.** The server emits a small, closed vocabulary of markup — `<span class="known-word"
data-term="…">` — and the client assigns it with `innerHTML`.

**This makes escaping a correctness requirement, not a nicety.** Paragraph text is scraped
from a third-party site and is therefore untrusted. The markup builder HTML-escapes every
character of source text *first*, then injects its own tags around the escaped runs. Scraped
text can never introduce an element or attribute. There is a test for this.

**Consequences.** Any future markup (translation ruby text, per-sentence anchors) goes
through the same builder rather than being concatenated ad hoc at a call site.

---

## D4 — Term normalisation for vocabulary matching

**Status:** Provisional · 2026-08-31

**Context.** Text contains `"Ephemeral,"`, `ephemeral's`, `Ephemeral—` and so on. A saved
word must match all surface forms, and the underline must land on the original characters.

**Decision.** A normalised form (lowercase, Unicode NFC, surrounding punctuation stripped,
trailing `'s`/`’s` removed) is the *key*; the original span in the paragraph is what gets
wrapped. Matching walks tokens and prefers the longest run, so a saved phrase like
`give up` wins over a saved bare `give`. Phrase length is capped (4 tokens) to bound the scan.

**Provisional because.** This does not do stemming — saving `run` will not underline `running`.
Proper lemmatisation needs a dictionary or a library, which conflicts with the
minimal-dependency preference. Flagged as an open question rather than silently assumed.
**Answered by D39**, which matches a saved word in all its forms without a library.

---

## D5 — No JavaScript/TypeScript runtime libraries *(superseded by D43)*

**Status:** Adopted · 2026-08-31

**Context.** Stated preference for minimal JS/TS libraries. The client already runs as
browser-native ES modules with no bundler.

**Decision.** The definition box, keyboard routing, and selection handling are hand-written.
The only npm packages are build-time: `typescript`, and `@microsoft/signalr` for its type
declarations (the SignalR client itself is a classic script from libman, and the type import
is erased at compile time).

**Consequences.** The comic-bubble pointer is CSS borders, not a drawing library. No virtual
DOM: the box is a handful of `document.createElement` calls.

---

## D6 — Keyboard handling becomes a modal router

**Status:** Adopted · 2026-08-31

**Context.** `j`/`k` already scroll the page. Once the definition box is open the same keys
must page through senses instead, scrolling must stop, and `s`/`t`/`d`/`Esc` need to mean
different things depending on what is on screen. More keys are planned.

**Decision.** A small `KeyboardRouter` holds a stack of named modes (`reading`,
`definition`), each a map from key to handler, with the innermost mode consulted first. This
is the TUI model the reader asked for, and it makes "ready for new actions on other keys" a
one-line registration rather than another branch in a growing `if` chain.

**Consequences.** Scroll-lock is a property of the mode, not a scattered boolean. The
existing `vim-motioned` scroller registers as the `reading` mode's `j`/`k` binding instead of
listening to the document directly.

---

## D7 — Definition box height is measured from the first sense and then locked *(superseded by D27)*

**Status:** Provisional · 2026-08-31

**Context.** Requirement, verbatim: "It's height should match first definition's text length."

**Interpretation.** The box is sized to fit the *first* sense's text, and keeps that height
while `j`/`k` page through the remaining senses, so the box does not jump or resize under the
reader. A longer later sense scrolls within the fixed frame.

**Provisional because** the requirement admits a second reading — that the box resizes to each
sense as you navigate. The chosen reading is the stable one and matches how a comic panel
behaves, but this is a guess and is listed as an open question.

---

## D8 — Prefetched chapters use a two-tier, user-scoped cache

**Status:** Adopted · 2026-08-31

**Context.** Requirement: opening a chapter of a new novel prepares that chapter *and* the
next; only the first is sent. The second waits in MongoDB under
`user-identifier/novel-name/chapterN`, and is promoted to an in-memory server cache once the
reader reaches it, with MongoDB then holding the chapter after that.

**Decision.** `IPreparedChapterCache` with a MongoDB tier (durable, survives restart) and an
`IMemoryCache` tier (hot, current chapter). The cache is user-scoped because the stored
artefact is *marked-up* text, and the markup depends on that user's vocabulary.

**Consequences.**
- Saving or deleting a word invalidates that user's prepared chapters — the underlines are
  baked in. Invalidation is by user prefix.
- `Microsoft.Extensions.Caching.Memory` is a first-party package; the minimal-dependency
  preference was about the browser bundle, not the server.

---

## D9 — User identity stays the current hardcoded name for now *(superseded by D20)*

**Status:** Deferred · 2026-08-31

**Context.** Every new feature is per-user, but the client hardcodes `userName = "Anton"` and
there is no auth.

**Decision.** Keep a single `userName` threaded through the new APIs exactly as the existing
hub does, so nothing is designed in a way that blocks real auth later. Do not invent an auth
system as a side effect of a vocabulary feature.

**Consequences.** Every new hub method takes the user name as its first argument. Swapping to
a real identity later means changing how that value is obtained, not the storage shape.

**Superseded 2026-08-31 by D20.** Readers now sign in, and the hub takes the
reader from the authentication cookie. The page no longer names anyone.

---

## D10 — Translation (`t`) answers with a server-side stub *(superseded by D31)*

**Status:** Adopted · 2026-08-31 (supersedes the earlier "wired but not implemented")

**Context.** `t` for translation was described as an example of future extensibility rather
than a feature to ship now, and no target language was given. The first version of this
entry claimed an `ITranslationProvider` seam; no such type was ever written — `t` only
reached a `console.info` on the client, so nothing exercised the round trip.

**Decision.** The round trip is real and the *answer* is the stub, not the plumbing. The hub
exposes `Translate(userName, surfaceForm)` and replies on `ReturnTranslation` with
`TranslationResponse.Stub`, a fixed string built from the term. The client shows
"translating…" while it waits and then renders the reply.

`TranslationResponse` carries `IsStub`, and the client turns that into a visible
"placeholder — no translation provider configured" note. A placeholder must never be able to
read as a real translation.

**Consequences.** Wiring a provider means replacing the body of one hub method; no client
change. `userName` is already on the wire so a provider can pick the reader's target
language. Free options when it is wanted: MyMemory (free tier, no key) or a self-hosted
LibreTranslate.

**Open question.** Which language pair, and translate the *word* or the *sentence around it*?

---

## D11 — Background work goes through a Domain-level seam

**Status:** Adopted · 2026-08-31

**Context.** Prefetch must never delay the paragraph a reader is waiting for, so it has to
run detached. But `NovelReader.Domain` has a zero-package rule, which rules out `ILogger`
there — and detached work that swallows its errors silently is a debugging trap.

**Decision.** `IBackgroundWorkScheduler` is declared in Domain and implemented in `WebApi`
as `BackgroundWorkScheduler`, which owns the `Task.Run`, the application-shutdown token, and
the logging of failures.

**Consequences.** Domain keeps its rule; failures are visible in the log; a prefetch that
fails is a missed optimisation the reader never sees. Measured: reading chapter 2 left
`testreader/test-novel/chapter3` waiting in the durable tier, as intended.

---

## D12 — Chapter roll-forward is bounded *(superseded by D19)*

**Status:** Adopted · 2026-08-31

**Context.** The original `NextParagraphProcessor` recursed into the next chapter whenever a
paragraph number was missing. At the end of a novel that recursion had no floor.

**Decision.** Roll-forward is a bounded loop (`MaxChapterAdvance = 5`) that ends in a clear
`InvalidOperationException` naming the novel and chapter.

**Consequences.** Running off the end of a novel now fails legibly instead of climbing the
stack or scraping chapter after chapter that does not exist.

**Superseded 2026-08-31 by D19.** The page loads whole chapters, so there is no
"that paragraph is past the end of this chapter" case left to roll forward from.

---

## D13 — Definition text is inserted with `textContent`, paragraphs with `innerHTML`

**Status:** Adopted · 2026-08-31

**Context.** Two different kinds of string reach the page and they need opposite treatment.

**Decision.** Paragraph markup is authored by the server, which escapes every scraped
character before adding its own tags (D3) — so the client assigns it with `innerHTML`.
Definition text is plain text from a third-party dictionary and is assigned with
`textContent`, never `innerHTML`. Wiktionary's HTML fragments are stripped server-side, and
the stripper removes tags *before* decoding entities so a decoded `<` can never be mistaken
for markup.

**Consequences.** The rule is per-channel, not per-call site, so it survives new UI. Verified
end-to-end: a seeded paragraph containing `<b>an old text</b> & smiling` was served as
`&lt;b&gt;an old text&lt;/b&gt; &amp; smiling`.

---

## D14 — The client patches underlines in already-rendered text

**Status:** Adopted · 2026-08-31

**Context.** The server marks up each paragraph as it is sent, but saving a word should
underline it in the text already on screen, not only in paragraphs still to come.

**Decision.** `reading/underliner.ts` walks the rendered text nodes and wraps or unwraps
occurrences of the changed term. It is a live patch over what the server already sent, never
the primary mechanism.

**Trade-off.** The client matcher is a word-boundary regex on the normalised term, so it is
slightly coarser than the server's token walk: saving `gu` underlines the `Gu` inside `Gu's`
without the `'s`, where the server would wrap the whole surface form. The difference
disappears on the next server-rendered paragraph, and the alternative — shipping the
tokeniser to the browser — is not worth it for a cosmetic edge case.

---

## D15 — `index.html` script order made explicit

**Status:** Adopted · 2026-08-31

**Context.** The page loaded `realTimeReader.js` (a module) *before* `signalr.min.js` (a
classic script). That worked only because module scripts are deferred while classic ones are
not, so the global happened to exist by the time the module ran — a subtlety no one should
have to re-derive.

**Decision.** The classic SignalR script is listed first, with a comment saying why. The
redundant separate `<script>` for `interactive-select/module.js` was dropped, since the entry
point imports it. `charset`, `viewport` and `<title>` were added, which the PWA phase needs
anyway.

---

## D16 — The navigation menu is a keyboard mode, not a focus trap

**Status:** Adopted · 2026-08-31

**Context.** The menu predated the keyboard router. It stayed visible only while one of its
`<li>`s held focus, kept its selection in `tabindex`, put an `<input>` directly inside a
`<ul>`, and stole focus on load. Restyling it to match the definition box meant deciding
whether to keep that mechanism.

**Decision.** It became a `NavigationMenu` that owns a stack of screens and renders the same
TUI panel as the definition box, driven by a `navigation` mode on the router (D6). It is
closed by default and opens with `n`; a small corner affordance advertises the key, since
the menu no longer announces itself by sitting open on the page.

Screens replaced the "generate the next list" call: a row's `run` can `push` another screen,
so the header can show a breadcrumb and Escape has somewhere to go back to.

**Consequences.** No more focus/visibility coupling, and no invalid markup. Escape now means
"up one level" inside the menu and only closes at the root, which is why an active filter is
cleared before a screen is popped. Both `/` and the old `?` reach the filter.

**Consequences for the chapter list.** "Go to chapter" reuses `GetNextParagraph` by resetting
the reader to the startup state, so it needed no endpoint — but with no way to ask how many
chapters a novel has, the list is a fixed window around the current chapter rather than the
whole book.

---

## D17 — One request at a time per reader, and one document per chapter

**Status:** Adopted · 2026-08-31

**Context.** Verifying Phase L turned up two faults that were really one story. A single
reader's session produced 64 `429 Too Many Requests` from the source site and left **28
copies of chapter 201 and 25 of chapter 206** in MongoDB, which then made
`FilteredCollection.TryGetExactlyOne()` throw `Sequence contains more than one element` on
every later read of those chapters.

The cause was not the reader asking for too much. Serving *any* paragraph schedules a
prefetch of the next chapter, so a chapter of 30 paragraphs queued 30 prefetches of the
chapter after it. The cache checks at the top of each one could not stop the pile-up: they
all looked before any of them had stored anything, all found nothing, and all went to the
site. Concurrent inserts then each wrote their own document.

**Decision.** Three changes, at three different levels:

1. **`UserRequestGate`** serialises `ProcessAndReturnAsync` per user name. One reader has one
   pair of eyes; their requests only ever need answering one at a time. Different readers
   never wait on each other.
2. **In-flight prefetch de-duplication.** `NextParagraphProcessor` claims a
   user/novel/chapter key *before* scheduling, and releases it when the work finishes.
   Claiming the key first is what makes it exactly one — a cache check cannot, because
   several racers all check before any of them writes.
3. **A unique index on `chapter`** in every novel collection, ensured once per novel per
   process, with the duplicates an earlier run already stored cleaned up first (keeping the
   most complete copy). `InsertOneAsync` now treats a duplicate-key error as "someone else
   stored it first" and returns their copy.

**Why all three.** (2) is what actually stops the storm. (1) is the invariant worth having
anyway — it makes a session's load predictable regardless of what the client does. (3) is the
one that does not depend on application code being correct: with it, no future bug can put a
second copy of a chapter in the database.

**Prefetch deliberately does not take the gate.** Holding a reader's gate to warm a chapter
they have not reached would delay the paragraph they are waiting for, which is exactly what
D11 exists to prevent. It is bounded by its own de-duplication instead.

**Consequences.** Measured on the same load that produced the failures: reading 25 paragraphs
now sends **one** request for the chapter and **one** for the prefetch, against 429s and
duplicate inserts before. The migration removed 73 duplicate documents on first start, and a
deliberate duplicate insert is now rejected with `E11000`.

`TryGetExactlyOne` keeps its `Single()`. It is an assertion of the invariant the index now
guarantees, and should still be loud if that invariant is ever broken.

**Not addressed.** The site intermittently answers `200` with a page containing no
paragraphs, which fails the reader's request outright. That is a separate robustness gap —
retry and backoff — left for its own change.

---

## D18 — Reading progress is one document per (user, novel)

**Status:** Adopted · 2026-08-31

**Context.** Progress was a single document per reader with every novel nested inside it:
`{ name, novels: { slug: { chapterNumber, paragraphNumber } } }`. That shape had three
faults, and the code around it had three more — `UpdateReadingProgressAsync` used
`SetOnInsert`, so a bookmark was written once and never moved; the update targeted the wrong
level of the document; and the numbers were written as strings but read with `AsInt32`.

**Decision.** Flat documents: `{ user, novel, chapter, paragraph, updatedAt }`, unique on
(user, novel), with a second index on (user, updatedAt) for recency.

**Why it matters beyond tidiness.** "Which novels has this reader read?" is now a `distinct`
on an indexed field rather than a read-and-parse of a growing document, and moving a bookmark
is a one-document upsert rather than a read-modify-write. Both are things the reading page
does constantly.

**Migration.** Old documents are spread into the new shape on start-up, before the unique
index is built — they carry no `novel` field, so they would otherwise all collide on it. The
string numbers are parsed on the way through.

**Consequences.** `distinct` returns its results unordered, so the recency order the menu
wants comes from a second projected read; the distinct list is what decides membership.
Mongo compares user names exactly while the account store treats them case-insensitively, so
a legacy row written as `Anton` is only reachable by an account registered as exactly `Anton`.

---

## D19 — The reader loads chapters, not paragraphs, and reports a settled bookmark

**Status:** Adopted · 2026-08-31

**Context.** The page fetched one paragraph per request and asked for the next as it
scrolled. Resuming where a reader left off needs a whole chapter on screen at once, and
replaying a bookmark one request per paragraph would be worse than what D17 just fixed.

**Decision.** `LoadChapter` returns a whole chapter in one call, and the hub method *returns*
its answer rather than pushing it, so the page can await one chapter before placing the next.
Opening at a bookmark loads that chapter — and the one before it when the bookmark sits within
`nearTopOfChapter` paragraphs of the start, so there is something above it to scroll back
into.

The wire now carries each paragraph's real number. It used to send `paragraphNumber + 1`, so
the number travelling with a paragraph was not that paragraph's own — which is unusable for a
bookmark, and was a trap worth removing anyway.

**The bookmark is debounced, not streamed.** Scroll events fire continuously; every one
restarts a 2-second timer and only the settled position is sent. Reading straight past a
paragraph sends nothing at all. Leaving the page flushes immediately, since there is no later
moment to do it in.

**Consequences.** `NextParagraphProcessor` became `ChapterReader`; the roll-forward loop that
bounded "that paragraph is past the end of the chapter" (D12) has nothing left to bound and is
gone. A chapter that will not load comes back `found: false` rather than throwing, because the
source site answers 200 with an empty page often enough to be a normal outcome.

---

## D20 — The hub takes its reader from the cookie, never from the client

**Status:** Adopted · 2026-08-31

**Context.** Every hub method used to take a `userName` parameter, and the page supplied the
hardcoded `"Anton"` (D9). With real accounts that becomes a hole rather than a placeholder:
any client could name itself anyone and read or rewrite their progress and vocabulary.

**Decision.** Accounts live in SQLite (`Microsoft.Data.Sqlite`, one table, no ORM) with
PBKDF2-HMAC-SHA256 password hashing. Sign-in establishes a cookie; the hub is `[Authorize]`
and reads the reader from `Context.User`. No hub method takes a user name any more.

Usernames are unique and case-insensitive, enforced by `COLLATE NOCASE` on the primary key —
the check-then-insert race is settled by the database, not by looking first. The stored
spelling is what the session carries, since it is the key every other store is written
against.

**Sign-in cannot be used to enumerate accounts.** "No such user" and "wrong password" return
the same failure and the same wording, and an unknown username is verified against a dummy
hash so both answers cost the same PBKDF2.

**Consequences.** D9's hardcoded identity is retired. `/ReadingPage` requires a signed-in
reader; `/Login` and `/auth/*` do not. A cookie challenge on `/auth` or `/signalr` answers 401
rather than redirecting, because a hub negotiate that receives the login page fails with a
JSON parse error rather than a useful one.

---

## D21 — A chapter can be asked for by number, and a miss stays a miss

**Status:** Adopted · 2026-08-31

**Context.** The chapter menu only lists a window of `chapterWindow` chapters either side of
where the reader is, because nothing reports how many chapters a novel has. Typing a number
outside that window matched nothing, so the one thing a reader most obviously wants to do —
go to chapter 400 — was the one thing the chapter list could not do.

**Decision.** `MenuScreen.itemsFromFilter` lets a screen build rows out of the filter text
itself, shown after the ordinary matches. The chapter screen uses it to offer any valid
number it is not already listing, marked "not in the list — try it". Whether that chapter
exists is settled by asking for it.

Parsing is deliberately strict — digits only. `Number` alone accepts `"1e3"`, `"0x10"` and
`"12.0"`, and offering to jump to chapter 1000 because someone typed `1e3` is worse than
offering nothing.

**The part that mattered more.** Making out-of-range attempts normal made the miss case
normal, and the miss case was wrong. For a chapter that does not exist the source site answers
**200** with a "some novel pages moved, use the search function" notice — inside the very same
`#content` div a chapter uses. So it scraped cleanly as a two-paragraph chapter, was **cached
in Mongo as if it were real**, and the reader was moved to it. The prefetch then did the same
for the chapter after it: one bad jump stored two junk chapters permanently.

`ParagraphsRetriever` now recognises that notice — marker phrases plus a paragraph count no
real chapter reaches — and throws. Throwing rather than returning empty is what keeps it out
of the cache: a chapter is only stored once the scrape succeeds. The hub already turns that
into `found: false`, and the page already says so.

**Consequences.** Both halves are guarded by the paragraph count *and* the phrases, so a
genuinely short chapter is still a chapter, and a chapter that happens to contain the words
"use the search function" is not mistaken for the notice. If the site rewords the notice the
markers stop matching and the junk-chapter behaviour returns — the tests pin the current
wording, so they would need updating alongside it.

**Verified.** Before the fix, one request for chapter 999999 left chapters 999999 and 1000000
in Mongo, both holding the notice. After it, the same request answers `found: false` and
stores nothing.

---

## D22 — Catalogue details are cached beside the bookmark, refreshed daily

**Status:** Adopted · 2026-08-31

**Context.** The library menu could only show a slug, because the progress row held nothing
else. The catalogue knows each novel's title, rank and chapter count, but asking it every time
the library opens would put a network round trip in front of the reading page's first paint —
and those three fields change slowly enough that yesterday's answer is almost always today's.

**Decision.** The (user, novel) progress row carries `title`, `rank`, `totalChapters` and
`detailsCheckedAt`. `NovelLibraryService` returns what is stored **immediately** and schedules
a background refresh for anything checked more than 24 hours ago — or never checked at all.
The reading page therefore never waits on the catalogue, and a day-old rank is not worth
delaying the first paragraph for.

Refreshes go through the **search** endpoint, not the novel's own page: search answers JSON,
while the page would have to be fetched and parsed as HTML for the same three fields. The
search term is the stored title when there is one, otherwise the slug with its hyphens turned
back into spaces. Only the result whose slug matches is taken — a search for "reverend
insanity" also returns other novels with those words in their names.

**The timestamp records the attempt, not the success.** A novel the catalogue cannot find, and
a catalogue that is down, both stamp it. Leaving it unset would mean retrying on every page
load, which is exactly how the request storm in D17 began. Nothing is overwritten on failure,
so the previous name and rank stay on screen and the retry happens tomorrow.

**Consequences.** Two writes now share a document, so neither may replace it: `SaveAsync`
(the bookmark, moving constantly) and `SaveNovelDetailsAsync` both `$set` only their own
fields. The earlier `ReplaceOneAsync` would have wiped the cached details on every scroll.

Refresh scheduling is de-duplicated per (user, novel) the same way prefetch is (D17), so two
page loads in quick succession queue one lookup per novel rather than two.

**Found while building this.** `SearchNovelsRetriever` bound `TotalChapter` against a field
the catalogue sends as `total_chapter`; `PropertyNameCaseInsensitive` does not bridge an
underscore, so every chapter count would have been null. Fixed with
`JsonNamingPolicy.SnakeCaseLower`. The retriever also dereferenced `Data` unguarded, and the
endpoint answers `{"data":null}` when it dislikes a request.

---

## D23 — Restoring a bookmark waits for the column to settle, but never waits forever

**Status:** Adopted · 2026-08-31

**Context.** Restoring a bookmark means measuring a paragraph and scrolling so its bottom
meets the bottom of the viewport. Both halves of that were wrong in ways only a browser could
show, and both were found the first time the page was opened in one.

**The font moves everything.** The reading column is set in a web font. Until it loads, every
paragraph is laid out at the fallback font's metrics and then shifts when it swaps in — so a
bookmark restored before that is measured against a layout that no longer exists. Observed
directly: the bookmarked paragraph ended up **160 px below the fold**, while re-running the
identical scroll a moment later landed it exactly. So `openNovelAt` now awaits
`document.fonts.ready` before placing the reader.

**A background tab never paints.** The first fix also waited a frame via
`requestAnimationFrame`, which in a hidden tab simply never fires. That turned a scroll that
was merely mistimed into one that never happened at all, and left `loadingChapter` stuck true
— wedging the chapter loader even after the reader came back to the tab. The frame wait is now
raced against a 100 ms timer, so it resolves either way.

**Decision.** `waitForStableLayout()` — await the fonts, then take whichever comes first of the
next frame or a short timer. Nothing on the open path may depend on a paint that a hidden tab
will not make.

**Consequences.** Verified in a hidden tab: the bookmarked paragraph's bottom now sits exactly
on the viewport bottom, and a bookmark near the top of its chapter loads the previous chapter
too, with the earlier chapter's text above it to scroll back into.

**The wider lesson.** Both bugs were invisible to the headless checks, which had verified the
*arithmetic* of "put this paragraph at the bottom" perfectly well. What they could not see was
that the arithmetic ran against a layout that was about to change, in a tab that was never
going to paint.

---

## D24 — Touch readers get tapped equivalents of the keyboard, gated on "no keyboard" *(its auto-define superseded by D26)*

**Status:** Adopted · 2026-09-04

**Context.** Every reader action is a keypress: `n` opens the menu, `d` defines a selection,
`s` saves it, `j`/`k` scroll, `t`/`Esc` and the rest live inside the panels (D6). On a phone
or tablet there is no keyboard, so the page was not merely awkward — it was inert. The three
things a touch reader needs: a way to open the menu, a way to reach a definition, and buttons
in the panels the keys drive.

**Detection is "no keyboard", not "small screen".** The trigger is
`(hover: none) and (pointer: coarse)`, not a width breakpoint. A narrow window on a laptop
still has a keyboard and must stay untouched; a large tablet has none and needs the controls.
The real axis is the input, so that is what is measured. `src/input/pointer.ts` reads it once
as `isTouchPrimary` and stamps a `touch-input` class on `<html>`.

**The switch lives in CSS wherever it can.** Most of the change is which chrome shows, so the
class does the work and the stylesheets carry both looks: the hint becomes a bordered button,
the menu grows a `✕` and hides the key-hint bar, the definition box shows a tap toolbar and
hides its hint bar, and the panels step up a size (the terminal-small chrome is a strain to
read and to tap). Only two things must be JavaScript, because they cannot be expressed as a
style: removing the affordance's `aria-hidden`/`tabindex` so it is a real focusable control
rather than a decorative hint, and the selection listener below.

**Selecting text is the `d` key.** With no `d` to press, a settled text selection inside the
reading column opens its own definition — the same `defineSelection` path the key takes.
`selectionchange` fires throughout a drag, so the lookup waits 450 ms for it to stop, fires
only for a selection whose anchor is inside the reading column (never one in the menu filter
or with a box already open), and is wired only when `isTouchPrimary`.

**Decision.** A `touch-input` class drives the chrome from CSS; the tap handlers are added
unconditionally (a mouse gains the same row-click and buttons, which is harmless and a small
win), and only the media-gated behaviour — the auto-define listener and the affordance's ARIA
— is guarded by `isTouchPrimary`. No library and no framework: the buttons are hand-written
DOM and the detection is one media query (D5). The menu still handles no keys itself and the
identity still comes from the cookie (D6, D20) — this adds a pointer, not a new authority.

**Consequences.**
- Desktop is pixel-identical: the affordance keeps `pointer-events: none` and its key badge,
  the menu keeps its hint bar and no `✕`, the definition box keeps its hint bar and no
  toolbar. Verified that `n` still opens the menu with the class absent.
- The `✕` and the reading-mode selection both mean exactly what Escape and `d` mean, so touch
  and keyboard never drift into two behaviours — the button calls `back()`, the listener calls
  `defineSelection`.
- Saving from a selection needs no touch button of its own: the selection opens the box, and
  the box's own Save button covers it. Scrolling needs none either — the native gesture
  already does what `j`/`k` do, and reaching the bottom still loads the next chapter.

**A note on verifying it.** The harness cannot toggle Chrome's pointer-media emulation, so the
touch state cannot be made real from outside the page. The chrome and every tap handler were
checked live by forcing the `touch-input` class and clicking; the media-gated auto-define was
checked with a faithful mirror of the listener — same debounce, same guards — reaching the
same proven define path. The one thing not exercised end to end on a real device is the
`isTouchPrimary` media match itself, which is a single query read once.

---

## D25 — Cache-busting is a versioned path prefix, because the client is an ES-module graph *(superseded by D43)*

**Status:** Adopted · 2026-09-04

**Context.** A touch-support change (D24) did not reach a phone that had already loaded the
site: the browser was still running a cached `realTimeReader.js` from before the change, so it
ran the old keyboard-only page. The client is served with no cache-busting, and because it is
an **ES-module graph** — the entry imports `./input/pointer.js`, which imports others — a
*single* stale file silently breaks the whole page, not just one feature.

**Why not a query string.** The obvious fix, `realTimeReader.js?v=hash`, does not work here.
A query on the entry script is not carried into the relative imports *inside* it: the browser
resolves `./input/pointer.js` against the module's own URL, without the query, so a changed
`pointer.js` stays stale. Rewriting every import to add a query would need a bundler or a
build step the project deliberately does not have (D5).

**Decision.** Version by **path prefix**, not query. Assets are named
`/_v/{token}/realTimeReader.js`, and the shells carry `<base href="/_v/{token}/">`, so every
relative URL — the `<link>`s, the entry `<script>`, the font `url()` inside the CSS, and every
`./…` import the modules make — resolves under the versioned path *on its own*, because a
relative specifier resolves against the versioned URL of the module that names it. Change the
token and the entire graph moves to fresh URLs at once.

- `AssetVersion` computes the token at start-up from every `wwwroot` file's path, size and
  last-write time — no file is read. `tsc` rewrites the emitted `.js` on every build, moving
  their timestamps, so the token tracks the build with nothing to bump by hand.
- Two static mounts: the versioned one (`RequestPath = /_v/{token}`) answers
  `Cache-Control: public, max-age=31536000, immutable`; the unversioned one is downgraded to
  `no-cache` so a direct hit or the favicon must revalidate rather than be trusted stale.
- The HTML shells are no longer static files. `ReadingPageController` and `LoginController`
  serve them through `VersionedPage`, which injects the `<base>` over an `<!--ASSET-BASE-->`
  placeholder and marks the shell `no-cache`. The shell is the one thing refetched each load —
  tiny — and it is what carries the reader onto the current token.

**Consequences.**
- A returning reader can cache every asset forever and still never run a stale one: the shell
  revalidates, hands them the current token, and the versioned URLs under it are new whenever
  anything changed.
- The token is per-process, computed once at start-up. A deploy is a restart, which is exactly
  when it must change; editing a `wwwroot` file *without* restarting will not move it, which is
  a non-issue since local iteration reloads anyway.
- All dynamic URLs are absolute (`/signalr`, `/auth/…`, `/ReadingPage`), so `<base>` — which
  only rewrites *relative* URLs — leaves the hub negotiate, the auth posts and the navigations
  untouched, and reshapes only the asset graph.
- Absolute-versioned URLs mean a client holding an old shell would ask for an old token and get
  a 404 rather than a wrong file; the shell's `no-cache` keeps that window to a single request.

**Verified.** The WebApi build is clean (0 warnings, 0 errors), and the running instance was
checked in a browser: the served `/Login` and `/ReadingPage` shells carry
`<base href="/_v/{token}/">`; the reading page rendered its 87 paragraphs — which only happens
if the whole module graph resolved, so the entry **and** its `./input/pointer.js` import both
loaded from `/_v/{token}/…`, with no app asset leaking to the unversioned root; the versioned
assets answered `immutable`, an unversioned direct hit answered `no-cache`, and a stale token
(`/_v/deadbeef/…`) 404'd. (The check ran against the developer's own instance because this
session's sandbox kills any process that binds a port — a build, which binds nothing, runs
fine.)

---

## D26 — On touch, a selection offers a button; it does not open the box

**Status:** Adopted · 2026-09-04 · supersedes the auto-define half of D24

**Context.** D24 gave a touch reader definitions by looking a settled selection up on its own,
on the reasoning that with no `d` key the selection *is* the gesture. On a phone that turned
out to be the wrong trade: selecting text is also how a reader marks their place, re-reads a
phrase or simply mis-taps, and every one of those was answered by a panel that covered the
text and locked the page. The lookup was never the problem — deciding *for* the reader that
they wanted one was.

**Decision.** A settled selection inside the reading column unhides a button in the top-right
stack, directly under the `n navigate` hint, reading **`d definition`**. Tapping it runs
exactly what `d` runs. Nothing opens by itself any more.

- **The corner is now a stack, not two absolutes.** `.reader-affordances` is the fixed
  top-right box; the navigation hint and the definition button are flex items in it with a
  gap. "Beneath the navigation hint" is then a fact of the layout rather than an offset the
  two rules have to keep agreeing on, and the hint's own `position: fixed` is gone.
- **The selection is captured when the button appears and re-measured when it is tapped.**
  `SelectedTerm` now carries a cloned `Range`. A tap on a corner button is exactly the sort of
  thing a browser may answer by dropping the live selection, and the reader may have scrolled
  between selecting and tapping; the clone survives the first and re-measuring handles the
  second, so the box's tail still points at the words.
- **It says `d definition` although a phone has no `d`.** The label is what the reader is told
  to look for, and it is what the desktop hint says for the same action; a second name for one
  action would be worse than a key badge nobody presses.

**Consequences.**
- Desktop is untouched again: the button is unhidden only from the `isTouchPrimary` wiring, so
  a keyboard never sees it, and `d` still does the work.
- One behaviour with two triggers, as D24 had it — the button and the key call the same lookup.
- The button hides itself whenever it would be wrong: the box is open, the menu is open (which
  covers that corner), or the selection is gone.

---

## D27 — The box opens on the request, not on the answer, and is always three lines tall

**Status:** Adopted · 2026-09-04 · supersedes D7

**Context.** The box was created when the definition arrived. Until it did, the page looked
precisely the way it looks when nothing was pressed at all — so a slow dictionary, a slow
network and a connection that had silently died were three states the reader could not tell
apart, and the only move available was to press `d` again.

**Decision.** The *request* opens the box. It shows `loading…` under the word, fills itself in
when the answer arrives, and after five seconds of silence says **`connection timeout`** in
red. Its body is a fixed three lines tall whatever it holds.

- **The timer lives in the box**, started by `open` and cleared by `showDefinition` or by
  closing. Nothing else has to remember that a lookup is outstanding, and a box that is closed
  cannot leave one behind.
- **A late answer is still accepted** and replaces the timeout message. The request was never
  cancelled — only its silence was reported — and with hub calls now waiting for a dropped
  connection (D28), the answer to a lookup made during an outage genuinely does arrive later.
- **Three lines in CSS, not the first sense measured (D7).** D7's reason — the panel must not
  resize under the reader — has become stronger rather than weaker: the content now changes at
  least twice, and the first thing in the body is the single short line `loading…`, so locking
  to what was measured first would guarantee the box jumped the instant the definition landed.
  A constant is the only height that never moves. Longer senses scroll inside the frame,
  exactly as they did under D7.
- **The waiting states are not the loaded state.** With no term yet there is nothing to save,
  delete or translate, so the hint bar and the tap toolbar offer only `esc close` until the
  definition is in.

**Consequences.**
- Three lines is the entire budget: a long sense, or a sense with an example and a translation
  under it, scrolls. That is the price of never moving, and it is the one the reader asked for.
- `--tui-error` joins the palette in `tui.css`, so the timeout message and the reconnecting
  notice (D28) are the same red rather than two hard-coded ones.
- A definition that takes longer than five seconds now *says* five seconds passed, which makes
  a slow dictionary provider visible for the first time (D1 measured Wiktionary at up to 3.7 s,
  so the threshold sits deliberately above that).

**Amended 2026-09-04: the width is fixed too, and the scrollbar gutter is held open.** Two
faults found reading on a phone, both when looking up a word at the right-hand edge of the
column, and both measured before and after the fix.

- **The panel was squeezed to a few characters.** It had a `max-width` but no width, so it
  shrank to fit — and a `position: fixed` box takes its available width from its own `left`
  to the right edge of the viewport. A word near that edge therefore left almost no room:
  measured **109 px** wide. Worse, it ratcheted: `#position` measured the panel *where it
  currently sat*, so each re-place (and opening on `loading…` guarantees at least one) read
  the squeezed width and pushed it further right again. The toolbar, whose buttons have a
  minimum width, then overflowed the panel it was inside. The panel now has a **width**,
  `min(46ch, calc(100% - 24px))` — the same promise the fixed height makes, extended to the
  other axis, and it makes `offsetWidth` mean the same thing wherever the panel is. Measured
  **462 px** at the same word afterwards. `100%` rather than `100vw` because for a fixed box
  that is the viewport *without* the scrollbar; `#position` reads `clientWidth` for the same
  reason, since `innerWidth` counts the gutter and would place the panel partly under it.
- **Both corner buttons jumped right as the panel opened.** Not overflow: opening a panel locks
  scrolling (`overflow: hidden`), which takes the scrollbar away, widens the viewport by its
  width, and moves everything anchored to `right: 12px` by exactly that much — measured **15 px**,
  which is precisely this machine's scrollbar. `html { scrollbar-gutter: stable }` holds the
  space open whether or not a scrollbar is drawn in it. Measured **0 px** afterwards.

The cost is that a short definition now gets a full-width panel rather than a snug one; a panel
that changes size is what both of these faults were made of, so a constant one is the trade.

---

## D28 — The client reconnects on its own, once a second, and calls wait for it

**Status:** Adopted · 2026-09-04

**Context.** Reading a chapter through to the end and scrolling into the next one reliably
needed a page reload. Two faults, one on top of the other:

1. **The connection never came back.** `new HubConnectionBuilder().withUrl("/signalr").build()`
   has no reconnection whatsoever. A connection dropped while the reader was reading — a phone
   locking its screen, a network switch, a proxy's idle timeout — stayed dropped, and every
   later call failed against a dead connection.
2. **The loss was then read as the end of the novel.** `loadChapter` catches a failed call and
   answers `found: false`, which is exactly the shape "there is no such chapter" has. The page
   latched `reachedEnd`, and no later scroll tried again even once the server was reachable.

**Decision.** Three changes, and one guard.

- **`withAutomaticReconnect`, a second apart, for as long as it takes.** The retry policy
  returns a fixed 1000 ms and never gives up.
- **Every hub call waits for the connection** before it is sent, up to 30 seconds, instead of
  throwing the moment it finds the connection down. A definition asked for during a blip is
  delivered when the blip ends; so is a bookmark.
- **`ChapterView.failed` separates a call that did not get through from a chapter that is not
  there.** Only a genuine empty answer sets `reachedEnd`. A failed load parks a retry that runs
  as soon as the connection is back — necessary because the reader is at the *bottom* of the
  page when it happens, so there is no further scroll event coming to try again with.
- **The guard: a 401 is not a network fault.** The auth cookie is gone, and retrying every
  second forever would hide a session that is simply over, so that one case stops the loop and
  returns the reader to `/Login`. It is checked in both places an error can surface — the retry
  policy's `retryReason` and `onclose`.

**Also visible.** A `reconnecting…` chip in the bottom-left corner, present only while the
client is trying to get back. Between it and the box's `loading…`, "the server is slow" and
"the server is gone" no longer look the same (D27).

**Consequences.**
- `start()` no longer fails on an unreachable server: it keeps trying and resolves when the
  hub answers, so a reader who opens the page a moment before the server is up gets their page
  rather than "Could not reach the server."
- Nothing had to be re-established on reconnect. The hub is stateless per connection — the
  reader comes from the cookie (D20) and chapters from Mongo — so a new connection id costs
  nothing and no subscription has to be replayed.
- **What actually dropped the connection was never identified**, and deliberately so: it was
  reported from a phone, where a WebSocket has a dozen ordinary ways to die. The client now
  recovers from all of them without needing to know which.

---

## D29 — The corner chrome is anchored against `vw`, because Firefox widens the page while you select

**Status:** Adopted · 2026-09-04

**Context.** Reported from a phone: selecting the right-most word on a line slid the `navigate`
and `d definition` buttons several pixels towards the edge of the screen, under the reader's
eye. Chrome on the same phone was fine; Firefox was not — and Firefox is what this reader uses.
The first suspicion was a stale cache (D25), which was wrong: the phone was asked, over USB,
which rules it had, and it was running the current CSS.

**Cause, measured on the device.** Firefox draws its text-selection handles as content *inside
the document* — Gecko's AccessibleCaret. A handle on the last word of a line therefore sits
past the end of the text, the page gains real horizontal overflow, and Gecko grows the layout
viewport to match. Chrome draws the same handles as native UI outside the document and never
does this, which is the whole of the difference.

| with the right-most word selected | phone (Firefox) |
|---|---|
| `visualViewport.width` | 378 — unchanged, the screen did not move |
| `innerWidth` | 378 → **401** |
| `documentElement.clientWidth` | 378 — unchanged |
| nav button's right edge | 366 → **389** |

**It cannot be clipped away.** The overflow appears on `html` alone: `body`, the article and the
paragraph each reported `scrollWidth === clientWidth` throughout. `overflow-x: clip` on the root
was applied live on the device and measured — the viewport still grew to 401. The caret is not
in any element's overflow to clip.

**Decision.** Stop the chrome trusting the layout viewport. Probing every length on the device
while a handle was on screen showed `%` following the inflated viewport (400.8) while `vw`
stayed honest (378) — so their difference *is* the phantom width, and adding it back cancels
the slide:

```css
right: max(12px, calc(100% - 100vw + 12px));
```

`max()` is what keeps a *desktop* scrollbar from pushing the buttons the other way: there `vw`
is the wider of the two and the calc goes negative, so the floor of 12px wins. The definition
box takes `min(46ch, calc(100vw - 24px), calc(100% - 24px))` for the same reason — neither
measure is honest in both situations, and the smaller of the two is right in each.

**Consequences.**
- The page still gains its phantom 23px; nothing here fights the engine. Only our own anchoring
  is made immune to it, which is why `right: 12px` must not be "simplified" back in.
- Measured after the fix, on the device, with a real long-press: nav and definition buttons both
  hold at 366 while `innerWidth` is still 401, and the box opens 354px wide at left 12.
- Desktop is unchanged: the stack computes to exactly `12px` there.

**How it was verified, for the next time.** Firefox for Android answers the Remote Debugging
Protocol over `adb forward tcp:6000 localabstract:org.mozilla.firefox/firefox-debugger-socket`;
a ~90-line client is enough to list tabs and evaluate JS in the live page. Driving the selection
with `adb shell input swipe X Y X Y 600` matters — a selection made from script does **not**
raise the native handles, and with a scripted selection this bug does not reproduce at all.

---

## D30 — Panel scrollbars are addressed to each engine separately, because neither takes the same instruction

**Status:** Adopted · 2026-09-04

**Context.** The scrollbar inside the definition box was thin and TUI-like in Firefox and a fat
default bar in Chrome — nothing styled it, so each engine drew its own. A reader who moves
between browsers should not have to notice which one they are in.

**Why one rule will not do it.** Two engine facts, both measured here rather than assumed:

- `scrollbar-width: thin` is **6px in Gecko and 10px in Blink**, and there is no standard way to
  ask for a number instead of a word.
- Blink **ignores `::-webkit-scrollbar` entirely while `scrollbar-width` or `scrollbar-color` is
  set**, so the pseudo-elements — the only way to give it a number — cannot be combined with the
  standard properties.

So the width can only be settled by giving each engine what it alone understands, and by keeping
each block away from the other.

**The gate is `-moz-appearance`, and the obvious gate is a trap.** The natural test,
`@supports selector(::-webkit-scrollbar)`, **answers true in Firefox** — measured: Firefox 154
took the Blink branch, dropped to `scrollbar-width: auto` and drew a 12px default bar, which is
worse than doing nothing. `-webkit-appearance` is true in both engines as well. `-moz-appearance`
is true in Gecko and false in Blink, and is the only one of the four probed that separates them.

**Decision.** In `tui.css`, once, for both panel bodies:

- `::-webkit-scrollbar` rules at the top level give Blink and WebKit the width outright
  (`--tui-scroll-width`, 6px — Gecko's `thin`, so the two agree on a number).
- `@supports (-moz-appearance: none)` gives Gecko `scrollbar-width: thin` and `scrollbar-color`.
- Gecko drops the pseudo-element rules as invalid; Blink never sees the standard properties.

**Verified** by rendering the same harness in both engines headlessly and reading the width each
reported: **6px in Firefox 154 and 6px in Chromium**, and 6px again in the real windowed Chrome
against the running app (it had been 10px with `thin`, and the default before that).

**Consequences.**
- The scrollbar is the one piece of panel chrome `tui.css` draws rather than tokenises, because
  the split is too easy to get subtly wrong in two places.
- A future Blink that drops the pseudo-elements would fall back to its default bar. Adding the
  standard properties outside the `@supports` block to guard against that would break today's
  Blink immediately, so the trade is deliberate.
- Both bars are `--tui-scroll-thumb` on `--tui-scroll-track`, square, in both engines.

---

## D31 — Translation is MyMemory, and the reader is asked for two things in the box itself

**Status:** Adopted · 2026-09-04 · supersedes D10

**Context.** D10 shipped the whole round trip with a stub answer, and left one open question:
which language pair, and the word or the sentence around it. The plumbing has been waiting for
that answer ever since. It is answered here as **the word** — the box translates the term it is
already showing a definition for — and sentence-level is left undone, because it would change
what the client sends and how the box renders it rather than just which provider is called.

**The provider is MyMemory** (D10 named it as a candidate). No key, no account, no signup form,
and nothing is verified: a request that carries an email address gets ten times the daily
character allowance of an anonymous one — 50,000 rather than 5,000. That is the whole reason
the reader is asked for one.

**Two things follow from the email not being an identity.**

- It is stored **per reader**, on the account row, because it is that reader's allowance being
  spent. Two nullable columns on the SQLite `accounts` table (the existing database is migrated
  in place; SQLite has no `ADD COLUMN IF NOT EXISTS`, so the column list decides).
- Both null means "never set up", which is exactly what makes `t` open a form instead of asking
  for a translation nobody can fetch. There is no third state to keep consistent.

**The form lives in the definition box, not in a settings screen.** The reader is already
looking at the word they want translated when they discover they need to configure something;
sending them somewhere else to do it, and then back, is the worse of the two designs. The box
becomes the form and returns to the definition when it is done or cancelled — Escape backs out
of the form without closing the box.

**Its keys are the navigation menu's keys.** Type to narrow the list, Enter to go forward,
Escape to back out. The email field takes Enter to advance (refusing to advance on something
that is not an address), the filter narrows the language list, Enter hands over to the list,
Enter there submits. A tap on a row does what Enter does, which is how the same form works on a
phone. Nothing here is a new interaction to learn.

**The first translation carries its own settings, because the two calls race.** Finishing the
form fires the save *and* the translation together. The translation therefore accepts an
optional email and language and uses them in preference to the stored ones; every later request
omits them. The alternative — waiting for the save to land before translating — costs the
reader a visible round trip on the first word they ever look up, to avoid a race that this
removes outright.

**The page's checks are a courtesy; the server's are the rule.** The form refuses a malformed
address before spending a round trip, and `TranslationSettingsValidator` refuses it again on the
way in, for both the save *and* the overrides above. The language list is defined once on the
server and sent with the reading session, so the list the reader picks from and the list the
server accepts cannot drift — there is a test that walks the offered list and asserts every row
validates.

**Out of allowance, MyMemory answers `200` with a shouted warning where the translation goes.**
That string is recognised and refused rather than rendered, which is the same mistake D21 was
written about: a provider's apology must never be presented as its answer.

**Consequences.**
- The body's fixed three lines (D27) are relaxed for the form only. That rule exists so a
  *definition* cannot resize under the reader; a form in the same frame is a different thing and
  has to be as tall as its fields.
- `e` opens the form again to edit, from the box, and appears in the hint bar and the touch
  toolbar — without it a touch reader could configure translation once and never change it.
- **Translations are not cached.** Definitions are (D2) and translations should be, for the same
  reasons; at one reader and 50,000 characters a day it is not yet a constraint, so it is left
  as the obvious next step rather than built now.
- Nothing is sent longer than 450 bytes: MyMemory takes 500, and a request that would be
  refused is not worth making. The four-word selection cap (D4) means this is unreachable today.

**Verified.** End to end against the live API from the reading page: `t` on an unconfigured
reader opened the form with the email field focused and 35 languages from the server; a
malformed address was refused with focus held; a valid one advanced to the filter; typing "rus"
narrowed the list to Russian; Enter handed over to the list and Enter there submitted. The
settings landed in SQLite, and **"experience" came back as "опыт"**. `e` reopened the form
prefilled, and Escape returned to the definition with the translation still in place.
136 tests pass, 24 of them new.

**Verified on a phone too**, with real touch and a real soft keyboard over adb: long-press to
select, the `d definition` button, `translate`, the form, typing, and the reply — `crocodile`
came back as `крокодил`.

**That run found four things the desktop checks could not.** Worth recording, because the
desktop verification was not careless — it read every value out of the DOM and every value was
right. What it could not see was the *layout*:

1. The `language` label collided with its field. The label box was `7ch`; the word is eight
   characters. Reading `input.placeholder` from the DOM says nothing about where it is drawn.
2. **The validation message was invisible exactly when it mattered.** It rendered at the foot
   of the form, which on a phone is behind the soft keyboard — so a refused email looked like a
   dead Enter key. It now sits directly under the email field.
3. Tapping the language list made Firefox open its **own** native picker while the click
   handler submitted underneath it, stranding the picker over the page. The click-to-submit is
   gone: touch picks from the native list and taps `save`, a keyboard presses Enter. One
   gesture each, and no fight with the browser.
4. **The translation arrived below the fold.** It is appended under the sense, inside a body
   three lines tall (D27), so on both platforms it landed out of sight — the desktop test read
   it from the DOM and never noticed. The box now scrolls it into view.

**The copy is one line.** The form first explained *why* an email was wanted; the reader of this
app is the person who built it and does not need telling. Cutting that also bought back three
lines of the language list from behind the keyboard, which is the better argument for it.

---

## D32 — A phrase is translated from the reading page, and the translation never waits for the definition

**Status:** Adopted · 2026-09-04

**Context.** D31 put translation inside the definition box, which meant it only existed for
things that *have* a definition — one word. A phrase has no dictionary entry, so the one case
where a translation is most useful was the one case with no way to ask for it.

**Decision.** `t` on a selection of more than one word translates it, from reading mode; on
touch a third corner button, `t translate`, appears under `d definition` for the same
selections. The definition is asked for at the same moment and shown in the same box, but
**nothing waits on it**: whichever answer arrives first is rendered, and the definition keeps
its own `loading…` line above the translation until it lands.

**Only for more than one word.** A single word already has `d`, and its translation lives
inside that box behind `t` — offering a second route to the same place would be clutter.

**Two things had to change to make "in parallel" true rather than nominal.**

- **Answers are matched by surface form, not by the normalised term.** The page used to pair a
  translation with its box by comparing the term the *definition* came back with. Asking for
  both at once breaks that: when the translation lands there may be no definition yet, and so
  no term to compare. `TranslationResponse` now echoes the surface form that was asked for.
- **SignalR serialises hub invocations per connection.** `MaximumParallelInvocationsPerClient`
  defaults to **1**, so the translation queued behind the definition — and a phrase's definition
  is slow, because it misses Wiktionary and falls through to the second provider (D1). Measured
  on a phone before the change: the definition gave up at the box's five-second mark while the
  translation, already answered by the provider in 1.4 s, was still shown as `translating…`.
  Raised to 4. **This does not weaken D17** — chapters are serialised by the `UserRequestGate`,
  not by this setting.

**Measured after the change**, on the phrase "could travel": the translation arrived at
**1.2 s** and the definition at **7.2 s** — six seconds apart, in one box, neither blocking the
other. Verified on the phone too, with real touch: the third button appears only once a second
word is in the selection, and the box opens showing `loading…` and `translating…` together.

**Two things to know about it.**

- **The definition's timeout message is now misleading in this case.** A phrase routinely takes
  ~7 s to resolve, so the box says `connection timeout` (D27's wording, chosen when the only
  reason for silence was a dead connection) while the connection is demonstrably fine — the
  translation arrived over it a second earlier. The message is the reader's own spec, so it is
  left as it is rather than quietly reworded.
- **MyMemory is a translation *memory*, not only a translator.** For short, common phrases it
  can return a crowd-sourced segment that is simply wrong: "It was" came back as "Because it
  seems impossible to walk", where "could travel" gave a clean "может путешествовать". The API
  reports a `match` score that could be used to refuse the poor ones; nothing does yet.

---

## D33 — Red is reserved for what is actually wrong

**Status:** Adopted · 2026-09-04 · refines D27

**Context.** D27 gave the definition box a five-second timer and, on expiry, the red message
`connection timeout`. That was right when the only reason for silence was a dead connection.
It stopped being right once phrases were translatable (D32): a phrase misses Wiktionary and
falls through to the slower second provider (D1), so **seven seconds is its ordinary speed**.
The box was calling a healthy connection broken several times an evening — measured on a phone
saying `connection timeout` a second *after* a translation had arrived over that same
connection. Reported, fairly, as looking like a low-quality product.

**Decision.** The timer no longer decides what the message says; it only decides *when* to say
something. What it says comes from whether the connection is actually up — which the page
already knows, because it tracks that state to drive the reconnect chip (D28).

| after five seconds | message | treatment |
|---|---|---|
| connection is up | `still looking…` | dim, like `loading…` |
| connection is down | `connection lost` | red |

The state is pushed into the box as it changes, so a wait that becomes a real problem turns red
without waiting for another timer, and one that recovers goes back to dim.

**Both entry points are covered by construction.** A definition is loaded from `d` and, since
D32, alongside `t` on a phrase — but both go through the same `open()` and the same timer, so
there is one state machine to get right rather than two.

**Verified**, both directions, on an uncached phrase and with the server stopped:

```
slow but connected     3999ms loading…   4999ms loading…   5999ms still looking…   (never red)
connection actually down                 connection lost, rgb(196,106,106), chip "reconnecting…"
```

**Consequence.** D27's original requirement is still met — the reader can still tell a slow
answer from a dead connection — but the two now read differently, and only one of them is
alarming. The wording is deliberately plain rather than reassuring: `still looking…` says the
thing is still happening, which is what a reader wants to know.

---

## D34 — Nothing in the reading column may be wider than the column

**Status:** Adopted · 2026-09-05

**Context.** Chapter 211 of Reverend Insanity broke the page on a phone. Three of its 162
paragraphs are a scene separator: **109 hyphens and nothing else**. Measured on the device,
that paragraph rendered **1461px** wide — and Gecko answers content that will not fit by
growing the *layout viewport* to hold it:

| on chapter 211, phone | before | after |
|---|---|---|
| widest separator paragraph | 1461px | **355px** |
| `innerWidth` (layout viewport) | **1472** | 378 |
| `documentElement.clientWidth` | 378 | 378 |
| horizontal overflow | **1094px** | **0** |
| `.reader-affordances` computed `right` | 1105.93px | **12px** |

**D29 was doing its job, and it was not enough.** Its formula kept the corner buttons correctly
placed *in layout coordinates* the whole time — measured at `right: 366` on a 378px screen,
having absorbed 1094px. But `position: fixed` is fixed to the **layout viewport**, and once that
viewport is 1472px wide against a 378px window the page can be panned sideways: the window
slides over the buttons and they leave the screen. No amount of anchoring fixes a page that can
be panned. The overflow itself had to go.

**Decision.** `overflow-wrap: anywhere` on the reading column's paragraphs.

**`anywhere`, not `break-word`.** Both break a long run during layout, but only `anywhere` also
shrinks the paragraph's **min-content width** — and it is the min-content width that the viewport
is grown to accommodate. `break-word` would have wrapped the dashes and still left a 1472px
viewport.

**And a separator is drawn as one.** A paragraph made only of separator characters is a scene
break, not prose; wrapped it would be three lines of dashes. The page recognises it and draws a
short centred rule instead. This is client-side, so it needs no re-scrape and applies to every
novel already cached.

**Consequences.**
- General, not specific to this chapter: any unbreakable run — a URL, a table of underscores —
  now wraps instead of taking the layout viewport with it.
- D29's compensation now sits idle at `12px` during ordinary reading. It stays: the caret
  overflow it was written for is anonymous content in the canvas, not an element, and cannot be
  wrapped by this or clipped by anything.
- The separator's own characters are hidden (`font-size: 0`), so the paragraph keeps its
  `data-chapter`/`data-paragraph` attributes and the bookmark still counts it (D19).

---

## D35 — A settings drawer, and the reader's own AI for definitions and translations

**Status:** Adopted · 2026-10-08

**Context.** Readers wanted to set the column's size and colours, and to have definitions
and translations come from an AI of their choosing, read against the paragraph the word sits
in. The dictionaries (D1) and MyMemory (D31) answer for a word in isolation. "Sneered" has
one meaning in a dictionary, but which one the author meant depends on the sentence.

**Decision: the drawer.** A panel on the left, opened by a `>` button in the top-left corner
(it turns into `<` to close) or by `c`, and closed by `c`, Escape, or a tap on the page beside it.
(D36 changes some of this: no slide, `c settings` as the desktop hint, and j/k/h/l keys.)
Its background is see-through and blurred, so the reader can watch the page change behind it.
The drawer knows nothing about any one setting. It is a scrolling column of `SettingsSection`s,
so a new option is a new section added to the list in `realTimeReader.ts`.

**Appearance is per device, in `localStorage`.** A font size that suits a phone is wrong on a
monitor. The page also has to paint in the reader's colours before it has heard from the
server, or a light page flashes dark on every load. So an inline script in `index.html`'s
`<head>` copies the three values onto `<html>` as custom properties before the first paint.
A field left at its default is *absent*, not a copy of the default. Reset removes the
property, and the stylesheet's own value comes back. The slider scales `#novel-paragraphs`
only. The body keeps its 1.8em, because every TUI panel sizes itself from the body.

**The AI is on the account, and the key is encrypted.** The key is stored in three columns
on the SQLite account row (service, model, key). The key is protected with ASP.NET Core Data
Protection before it is written, and it is never sent back to the page; the page sees only
its last four characters. The key ring sits beside the accounts database
(`novelreader-keys/`), so no deploy can keep one and lose the other. If the key can no longer
be decrypted, the AI counts as not configured, and the reader enters the key again.

**Services are a fixed list, not a URL.** If the reader could type a base URL, they could make
the server call any address, its own network included, with a key in the header. Every
service on the list speaks the OpenAI chat completions dialect, so one client serves them all.

**Context, and what is not done with it.** While an AI is set up, the page sends the
paragraph's text with every definition and translation request (capped at 2000 characters
server-side). AI definitions are **not cached**, because an answer about one word in one
paragraph is not the word's meaning for every reader (D2). If the AI fails, the dictionary or
MyMemory answers instead. The box names its source, so the reader can see that it fell back,
and the server logs the service's reason.

**Three things measured, not assumed.**
- `reasoning_effort: "none"` makes glm-5.3 answer directly: a translation in fifteen tokens.
  Without it, the model spent 400 tokens reasoning and was cut off before answering. A model
  that rejects the field gets a 400, is retried once without it, and is remembered.
- DigitalOcean's gateway answers **403 "not authorized"** to
  `Content-Type: application/json; charset=utf-8`, the default `StringContent` sends. The
  same key and body pass without the charset parameter.
- No temperature and no token limit are sent. Newer OpenAI models refuse both under their old
  names.

**Consequences.**
- glm-5.3 takes about 5–10 s for a definition, so the box's dim `still looking…` (D33) is
  the ordinary case for an AI definition, not the exception.
- Translating with an AI still needs the target language from the D31 form, and that form
  still asks for an email the AI does not need. That is left as is for now.
- Data Protection also signs the auth cookie. Persisting the key ring means readers stay
  signed in across restarts, but everyone is signed out once, the first time this deploys.
- On touch, the definition box does not show who answered. The hint bar names the source,
  and touch hides the hint bar. A translation's note does show it (`ru · glm-5.3`), but if the
  AI fails on a definition, a phone reader cannot tell that the dictionary answered instead.
- Escape closes the drawer even from inside one of its fields. That is deliberate: the
  router ignores keys while a text field has focus, so without its own Escape handler the
  drawer could only be closed with the mouse after typing a colour.

**Verified on a phone** (Galaxy S23+, Firefox, 1080×2340):

| step | result |
|---|---|
| tap `>` | drawer slides in, button becomes `<`, page blurred and visible behind it |
| swipe inside the drawer | body scrolls down to the API key field and save/remove |
| type `f4ecd8` as background | page turns cream as it is typed; the drawer's transparency is plain against it |
| drag the slider | 155%, column re-set live, reader kept on the same paragraph |
| `reset to defaults` | back to `#1b1b1b` / `#b2b2b2` at 100%, storage entry removed |
| tap the dimmed page | drawer closes |
| long-press "lanterns", `d definition` | definition from glm-5.3 |
| `translate` | `фонари`, noted `ru · glm-5.3` |

One fix came out of it. At phone width, the key field's placeholder was cut off at "…keep the
saved". It now reads `empty keeps saved key`.

---

## D36 — The drawer is a keyboard grid, and the reading position survives a re-layout

**Status:** Adopted · 2026-10-08

**Context.** Four things came out of using D35:
- The drawer slid in, and nothing else on the site animates.
- On a desktop, its `>` button with a `c` badge looked like nothing else on the page.
- From the keyboard j/k only scrolled the drawer, and there was no way to reach a field
  without the mouse.
- A phone switched off in one orientation and back on in the other opened at a random place in
  the chapter. That bug was older than the drawer.

**Decision: no animation.** The drawer appears at once, and its CSS has no transition left.

**Decision: the corner hint matches the other corner.** On a keyboard, the top-left reads
`c settings`, drawn exactly like `n navigate` on the right: dim, decorative, out of the tab
order, hidden while the drawer is open. Only on touch is it a button showing `>` and `<`.

**Decision: a grid of rows.** j/k move from row to row (font size, background, text colour,
reset, service, model, key, save/remove). h/l move along a row: a colour's swatch to its hex
field, `save` to `remove`. On the slider, h/l change the font size directly. Enter edits the
field under the cursor, or presses the button. In a field, Escape or Enter goes back to
choosing, and Escape while choosing closes the drawer. The cursor is drawn like the navigation
menu's selected row (accent outline, the row's caption lit with `>`). The hint bar names only
the keys that do something where the cursor is: `h/l smaller/larger` on the slider,
`h/l across` on a row with more than one control, nothing on a row with one.
Rows are found from the DOM (`.settings-row`, `.settings-actions`), so a new section gets the
keys for free.

Enter in a field no longer submits the AI form. It means "done with this field", the same as
everywhere else in the drawer, and `save` is one row down.

**Decision: a font-size change keeps the bottom paragraph in place.** Each change notes the
paragraph at the bottom of the screen, which is the one the bookmark records. It applies the
change, puts that paragraph back at the bottom, and checks again 200 ms after the last change.
The paragraph noted at the start of a burst (a drag, h held down) is kept for the whole burst,
so rounding cannot drift it step by step. Measured on desktop: 100% → 160% → 110% with the
same paragraph at the bottom throughout, 0px off.

**Decision: a rotation restores a position recorded before it.** The old handler read "which
paragraph is on screen" at `orientationchange`. After a phone wakes in the other orientation,
the text has by then re-wrapped at the new width under the old scroll offset, so that
reading was a paragraph the reader had never seen. `ReadingAnchor` now records the paragraph
at the top of the screen, and how far into it the reader is, on every scroll, but only while
the column is still the width it was recorded at. When the width changes, from `resize`,
`orientationchange`, `pageshow` or the page becoming visible, it puts that position back. It
repeats over 700 ms (on timers, which work in a hidden page, D23), because a phone re-lays the
page in several steps. A change of height alone, such as a toolbar sliding away, re-wraps
nothing and is ignored. `history.scrollRestoration` is now `manual`, so the browser's own
memory of an offset taken at another width cannot be replayed on top.

**Verified on the phone** (Firefox, S23+). Read into a chapter, sent Firefox to the
background, rotated, and brought it back: portrait → landscape landed on the same paragraph at
the same line. Landscape → portrait landed on the same paragraph, about two lines further in.

**Consequence.** Rotation uses the top of the screen, and a font change uses the bottom. That
is deliberate. A rotation should keep the line being read. A font change should keep the
bookmark's paragraph, which is what the reader asked for.

---

## D37 — The page holds at most four chapters

**Status:** Adopted · 2026-10-08

**Context.** The page only ever grew. A long session left dozens of chapters on it, and
finding a word saved earlier meant scrolling through all of them.

**Decision.** Keep the chapter being read, at most **two whole chapters above it**, and the one
already loaded below it: **four at most**. When the reader settles (the bookmark report, D19)
and whenever a chapter is appended, everything older is removed. Whatever else happens, the
oldest chapters go until no more than four remain. Only chapters above the one being read are
ever removed, so everything removed is off the top of the screen. The paragraph on screen is
measured before and after, and the scroll offset is corrected by the difference. If the
browser's own scroll anchoring has already done that, the difference is zero.

**Verified** by scrolling through eight seeded chapters on desktop. The page held
`1,2` → `1,2,3` → `1,2,3,4` → `2,3,4,5` → … → `5,6,7,8`, and the paragraph at the bottom of the
screen did not move at any trim.

**Consequences.**
- Scrolling up stops at the top of the oldest chapter kept. There was never upward loading,
  so this is only a shorter stretch. The navigation menu still reaches any chapter.
- The bookmark is unaffected: it is read from the paragraphs on screen, which are never the
  ones removed.

---

## D38 — Chapter titles have their own size

**Status:** Adopted · 2026-10-08

**Context.** The title (paragraph 1, D19) is drawn at 2em of the column, or 1.35em on touch, so
it grew and shrank with the font-size slider. A reader who wants larger prose doesn't
necessarily want a larger heading. The heading takes most of a phone screen at 2em.

**Decision.** A second slider, `chapter title size` (60–200%), under `font size`, stored per
device next to the other appearance values as `titleScale`. The two are **independent**. The
title sits inside the column, whose size already carries `--reader-font-scale`, so the title's
rule divides that factor back out and applies only its own:
`calc(2em * var(--reader-title-scale, 1) / var(--reader-font-scale, 1))`. A title-size change
goes through the same keep-the-bottom-paragraph path as a font-size change (D36), because a
taller heading pushes the chapter down as well. Reset clears both.

**Verified** on desktop: default 58 / 29px (title / text); text at 120% → 58 / 35; title at
70% → 40 / 35; reset → 58 / 29 and storage cleared. On the phone, the slider shows up as a
second row and the keyboard grid (D36) gives it h/l with no extra code.

---

## D39 — A saved word is matched in all its forms

**Status:** Adopted · 2026-10-09

**Context.** D4 matched a saved word exactly, so a saved `sneered` underlined only `sneered`,
never `sneer`, `sneers` or `sneering`. That was left as an open question because proper
lemmatisation seemed to need a library.

**Decision: generate forward from a base form; don't stem.** Each saved word gets a **base
form**. Every form of that base is generated (`sneer` → `sneers, sneered, sneering`), and each
form maps back to the word as saved. The underline on `running` therefore carries `run` and
opens the word the reader actually kept. A stemmer was rejected because it cuts unrelated
words to one stem (`general`/`generous` → `gener`, `university`/`universe` → `univers`) and
underlines words nobody saved, and it cannot get from `ran` to `run`.

**Where the base form comes from.**
1. **The dictionary, at save time.** The definition is usually cached, since the reader has
   just looked the word up. Wiktionary names the base: `sneered` is "simple past and past
   participle of sneer". How much the entry is trusted depends on its *main* sense (the first
   that is not a symbol or abbreviation; `ran` is first "ISO 639-3 language code for Riantana"):
   - an inflection note **in** the main sense is the answer (`sneered` → `sneer`,
     `tables` → `table`);
   - a main sense that is a **noun** makes the word a base in its own right, and a note
     further down is ignored. `evening` is a time of day first and "present participle of
     even" only later; taking that would underline every "even";
   - any other main sense (an adjective, say) carries the verb's meaning, so a note further
     down is taken (`sneering` → `sneer`, `interesting` → `interest`).

   A named base is used only if it really generates the saved word.
2. **The suffix rules, when no dictionary has an entry at all.** Without a lexicon there is no
   single answer (`debated` is `debat`+ed or `debate`+d), so *every* candidate whose own forms
   include the word is kept, and the word matches in the forms of all of them. The wrong
   candidates are not words, so their forms never occur in text. Generating forward is what
   tells `hoping` (`hope`) from `hopping` (`hop`): a one-syllable base doubles its last
   consonant, so `hop` gives `hopping` and never `hoping`.

**The forms.** Regular `-s`/`-es`/`-ies`, `-ed`/`-d`/`-ied`, `-ing` (with the e dropped, the
consonant doubled, `ie`→`ying`, `c`→`ck`), and `-f`/`-fe` → `-ves`. A table of about 150
irregular verbs (`run`/`ran`) and a few irregular plurals (`child`/`children`). For a phrase,
the first word is inflected (`give up` → `gave up`, `giving up`). Two kinds of form are left out
on purpose:
- regular `-ed` for irregular verbs, which made `be` → `bed` and `see` → `seed`;
- `-er`/`-est` except for `-y` adjectives (`happier`), because plain comparatives land on
  unrelated words: `corn`/`corner`, `show`/`shower`, `flow`/`flower`, `for`/`forest`. So
  `darker` does not match `dark`.

**Keeping it consistent.**
- The base form is stored on the vocabulary entry (`base` in Mongo).
- Words saved before this get theirs from the dictionary in a background backfill, once per
  reader per process, started when the page opens.
- After a save, the page receives every form, so text already on screen is underlined
  exactly as the server will mark up the next chapter.
- Deleting by any form removes the saved word.
- `ParagraphMarkupBuilder.Version` (now 2) is stored with each durable prepared chapter, and a
  mismatch is a miss, so chapters marked up under exact matching are prepared again.

**Verified** against live Wiktionary entries in the local app. `sneering`, `stopped`, `tables`,
`ran`, `hoping` and `interesting` got `sneer`, `stop`, `table`, `run`, `hope` and `interest`;
`evening`, `morning` and `building` kept themselves. On the page, saved `ran` underlined
`running`, saved `hoping` underlined `hoped`, and saved `evening` left `even` alone. The server
markup after a reload matched the live underlines.

**Consequences and known gaps.**
- No Domain dependency; adding an irregular verb is one line in `WordForms`.
- `-er`/`-est` comparatives other than `-y` adjectives, and irregular words missing from the
  table, still match only as saved.
- **A dictionary outage is cached as "no such word" (D2), and that sends a word to the suffix
  rules for good.** During testing a timeout did exactly that to `evening`, which then matched
  `even`. The cure belongs in the definition cache: tell a provider that failed apart from one
  that answered "not found". That is not done yet.

## D40 — A password is changed from the settings drawer, the new one typed once

**Decision.** The settings drawer has a `password` section: the current password, the new
one, each with an eye button beside it that shows what was typed, and `change`. There is no
"repeat it" field — the eye does that job. The current password is mandatory, even though
the cookie already says who this is, so a session left open on someone else's screen cannot
be turned into the account.

**How.** `RealTimeReaderHub.ChangePassword(currentPassword, newPassword)` →
`AccountService.ChangePasswordAsync`, which verifies the current password against the stored
hash → `IUserAccountRepository.UpdatePasswordHashAsync`. Like every hub method it acts on the
caller the cookie names, so it cannot be pointed at another account (D20). The 8-character
floor is the sign-up one. A wrong current password answers `current-wrong` and is logged as a
warning. Both fields are cleared and hidden again whenever the drawer opens.

**Consequences and known gaps.**
- Wrong current passwords are limited too (D41), on a count of their own.
- Other devices stay signed in after a change: the cookie carries no security stamp, so there
  is nothing to invalidate them with. Signing everyone out means rotating the key ring (D35).

## D41 — Five wrong passwords lock a name, for longer each time

**Decision.** Five wrong passwords for a username lock it for five minutes. If the guessing
carries on after the lock runs out, the next five earn ten minutes, then 20, 40 … up to a
day. A right password clears the record, and so does a day with no attempts, counted from
the end of the last lock. The password change's current-password check (D40) has the same
rules on a count of its own. A shared count would let anyone at the sign-in form stop the
signed-in owner from changing the password, just when they most need to.

**How.** `SignInThrottle` (Domain, in memory, keyed case-insensitively like the accounts
table). `AccountService` claims an attempt *before* checking the password, and a success
wipes the record. Counting each attempt up front is what makes the limit hold under a
burst of parallel guesses. Counting afterwards would let them all pass the "is it
locked?" check first. A locked sign-in answers `429` with `Retry-After` and "Too many wrong
passwords. Try again in N minutes."; the hub answers `locked` with the minutes.

**Not telling which names exist.**
- Names that have no account are tracked and locked exactly like real ones, with the same
  answer. If only real accounts could be locked, the lock would give away which names exist.
- A lock is checked before the account lookup and answers without hashing, so it is
  instant either way.
- An unknown name is still verified against a dummy hash (as before), so it costs a full
  PBKDF2 like a wrong password does. The dummy hash is now built at start-up — `Program`
  resolves `AccountService` eagerly. Built lazily, the first unknown name would have paid
  for two PBKDF2s.
- Measured against the running app: wrong passwords for 12 real accounts and 12 unknown
  names, 36 each, came back in a mean of 139 ms and 140 ms.

**Consequences and known gaps.**
- Anyone can lock anyone out by typing their name with wrong passwords. That is the price
  of a per-name lock. The growth means it costs the attacker more to keep up, not that it
  stops them.
- The lock is per name, not per address, so trying one common password against many names
  is not slowed at all. An address-based limit (ASP.NET's rate limiter) would be the next layer.
- In memory: a restart forgets every lock. Tracked names are swept once there are more than
  10,000, removing the ones that have gone quiet — at most once a minute, or a table of
  10,000 names that are all still recent would be walked on every attempt.
- Sign-up still says "That username is already taken." — it has to, for a sign-up to make
  sense — so the throttle stops name discovery through sign-in, not through sign-up.

## D42 — Hardening after the security audit

An audit of the code and of the live site at primotibalt.top/reader found nothing open, but
several missing layers. This adds them.

**Invite-only sign-up.** A new account needs an invite code. You create one on the server
with `NovelReader invite`, which prints the code and the accounts database it wrote to. Run it
from the app's directory with the app's environment, or it opens a different database.
- A code is 12 characters from an alphabet with no look-alikes (`K7QM-X2PD-9RTA`). It works
  once and expires after 24 hours. Case, dashes and spaces are ignored when it is typed.
- Only its SHA-256 is stored, in the `invites` table. Spent rows record who used them.
- The invite is spent in the same SQLite transaction that creates the account, by a
  conditional `UPDATE`, so two sign-ups cannot both use one code. A taken name rolls the
  transaction back and leaves the code unspent.
- The invite is checked before the name, so only someone holding a good code can learn that
  a name is taken. Sign-up no longer reveals which names exist to everyone else.

**Limits per client address** (`AuthRateLimits`, ASP.NET's rate limiter): sign-in 10 a
minute, sign-up 5 an hour. They answer 429 with `Retry-After` and a message the login page
shows. They stop one address guessing across many names, which the per-name lock (D41)
cannot, and stop it keeping the CPU busy with PBKDF2. Passwords are capped at 128
characters and `/auth` bodies at 4 KB.

**Novel names must be slugs** (`NovelSlug`: `^[a-z0-9][a-z0-9-]{0,119}$`). The name becomes
a path on the source site and a Mongo collection name. Unchecked, a signed-in reader could
have the server fetch any path on that site, or create collections without end. The hub
checks it in `LoadChapter`, `GetNovelProgress`, `ReportProgress` and `SaveWord`, and
`ChapterPreparationService` checks again where the name is used.

**Browser protections** (`SecurityHeaders`):
- A Content-Security-Policy allowing this origin only. It is the backstop for the one place
  the page writes HTML (D3). The pre-paint inline script in `index.html` is allowed by its
  hash, computed at start-up from the file, so editing that script needs nothing else
  changed.
- `frame-ancestors 'none'` and `X-Frame-Options: DENY`, `nosniff`,
  `Referrer-Policy: same-origin`, a Permissions-Policy, and
  `Cross-Origin-Opener-Policy: same-origin`.
- Verified in headless Chromium: both pages load with no CSP violations, the inline script
  applies the stored appearance, and the hub's WebSocket connects.

**Same-origin requests only** (`SameOriginRequests`). The hub, and any request other than
GET or HEAD, is refused with 403 if its `Origin` names another origin. SameSite=Lax keeps
the cookie off requests from other sites, but not from other pages on the same site
(`/cv`, subdomains).

**Smaller items:**
- The auth cookie is `Secure` always outside Development.
- Sign-out is POST-only; the page posts and then goes to the login screen.
- No `UseHsts`: nginx's header covers the domain, and the app's shorter one contradicted it.
- `AllowedHosts` is `primotibalt.top;localhost;127.0.0.1;[::1]`.
- `X-Forwarded-Prefix` now goes through the framework's forwarded-headers handling, which
  trusts only the loopback proxy. It replaces a hand-written middleware that accepted the
  header from anyone and kept only the first character of a prefix that lacked a leading
  slash.

**Consequences and known gaps.**
- If nginx ever forwards a `Host` other than those listed, every request gets 400. Add the
  name to `AllowedHosts`.
- The rate limits rely on nginx's `X-Forwarded-For`. If nginx stopped sending it, every
  client would share one budget, so 10 sign-ins a minute for everyone.
- Rate-limit counters, like the D41 locks, are in memory and reset on restart.
- Not done: moving the reader to its own subdomain (it still shares an origin with `/cv`),
  revoking sessions, and encrypting the key ring at rest.

---

## D43 — The client is Angular

**Status:** Adopted · 2026-10-19 · supersedes D5 and D25

**Context.** The hand-written client had grown to about 5,000 lines of TypeScript building its
DOM with `createElement`, and a vocabulary review was about to add the largest panel yet. The
reader asked for the client to be moved to the latest Angular first, and for the move to change
nothing a reader can see — on desktop or phone — before the review was built on it.

**Decision.** Angular 22 in `WebApi/client`: standalone components, signals, zoneless change
detection, `OnPush`. `ng build` writes content-hashed files into `wwwroot`, which is now build
output only and gitignored. The SignalR client is an npm import, so libman is gone.

- **One shell, two routes.** Both controllers serve the one `index.html` through `ClientShell`,
  with `<base href>` set to the path base; the router lazy-loads the login or the reading page.
  The login page does not download the reader.
- **Cache-busting by file name** replaces D25's versioned prefix: Angular hashes every script,
  stylesheet and font, so `ClientAssets` serves hashed names as `immutable` and the rest
  `no-cache`. `AssetVersion` and `VersionedPage` are gone.
- **No component styles.** Angular injects a component's styles as `<style>` tags, which the
  CSP (D42) blocks. The existing stylesheets became global styles, in the same order, so the
  look carried over unchanged. `inlineCritical` is off for the same reason. The login and
  reading `body` rules are scoped by a class each page sets on `<body>`.
- **The column is Angular, its paragraphs are not re-rendered.** An `@for` renders paragraphs;
  `ServerMarkup` writes each one's server markup once (D3, bypassing the sanitiser, which would
  strip `data-term`), and the underliner keeps patching it in place (D14). Code that changes the
  column and then measures runs `detectChanges()` first. The measuring helpers
  (`ProgressReporter`, `ReadingAnchor`, `SmoothScroller`, selection, underliner) were kept as
  they were: they are DOM code, and rewriting them would only have risked D23/D36/D37.
- **Same keys, same router.** `KeyboardRouter` is an injectable with the D6 semantics; panels
  still expose `move`/`activate`/`back` and the page binds them.

**Verified.** A Playwright script drove the old client and then the new one against the same
server, in desktop Chromium (1280×800) and an emulated touch phone (412×915, `hover: none`,
`pointer: coarse`): sign-in; bookmark restore; navigation menu and its chapter screen, the
`300` filter row and the library; the settings drawer, its keyboard grid and `h/l` on the font
slider keeping the bottom paragraph (6:2 → 6:2), reset clearing storage; the definition box —
senses, save underlining, the translation form opening focused on email, cancel, delete
removing the underline; touch `d`/`t` affordances, `t` only for a phrase; j-scrolling; rolling
into the next chapter. Every check matched and neither page logged an error or a CSP
violation. Screenshots were compared side by side; the one difference, the phone column's
margins, came from the portrait `body` rule losing to the new scoped one, and was fixed.

**Consequences.**
- A component that needs CSS adds it to a global stylesheet under `client/src/styles/`.
- A page left open across a deploy can fail to load a lazy chunk the deploy deleted, as a
  stale module graph could before D25; a reload fixes it.
- The build needs Node and an `npm ci` of about 500 packages; `-p:SkipClientBuild=true` still
  reuses what is in `wwwroot`.
- `AddMongoClient` no longer insists on an X.509 client certificate; without one configured it
  uses the connection string's credentials, so a local Mongo works again.

---

## D44 — A vocabulary review: Anki's schedule, the reader's AI as examiner

**Status:** Adopted · 2026-10-19

**Context.** Saved words were only underlined when met again; a word not seen for months was
forgotten. The reader wanted tests, scheduled the way Anki schedules cards. They wanted the
reader's own AI to help, since definitions are too long to type back. Answers should be in the
reader's own words, possibly spoken. Words should start with matching to definitions and move on
to sentences, and to translation from the reader's own language when one is set.

**Decision.**
- **Where:** the first section of the settings drawer, shown only while an AI is configured,
  with the due and new counts and `start review`. The review opens over the page as a TUI
  panel, like the others.
- **The server decides** which words, in which form, and whether an answer was right. A match
  is checked one pair at a time, so its key never reaches the page.
- **Schedule:** Anki's SM-2 variant with Anki's default deck options (`ReviewScheduler`); the
  20-new-words-a-day limit counts in UTC days. No fuzz, so the schedule can be tested exactly.
- **Form follows maturity** (`ReviewPlanner.KindFor`): match while new or learning or under 3
  days; explain in your own words until 10 days; then a sentence of your own, alternating with
  translating a sentence from your language into English when a language is set (D31).
- **Grading:** a match pair right at the first try is Good, otherwise Again. A free answer is
  judged by the AI as correct, partial or wrong, which gives Good, Hard or Again. "Don't know"
  is Again. The AI never awards Easy; that would be the AI guessing at confidence.
- **Glosses** for matching are written by the AI in one batched call, at most 12 words each,
  and stored on the card, so each word costs one call ever. When the AI cannot give one, the
  dictionary's first sense stands in, cut to 140 characters.
- **Voice:** the browser's speech recognition, in English, behind a mic button and `v`. Firefox
  has none, so there the button is not shown; a phone keyboard's own dictation works in the
  field anyway. The Permissions-Policy now allows the microphone for this origin.
- **Storage:** `Users.ReviewCards`, one document per (user, term), unique. Cards are created
  lazily for saved words and dropped when the word is deleted. The open exercise is kept in
  memory and lost on restart, which costs nothing because ungraded means unchanged.

**Verified.** 15 unit tests cover the scheduler's arithmetic, the planner's order and forms,
reading the AI's JSON, and a session end to end against fakes. All 287 tests pass. In the
browser, with the OpenAI endpoint answered by a local fake (an HTTPS proxy, its CA trusted only
by the test process) and Mongo a throwaway local instance, the flow was driven on desktop and
phone. The section is hidden without an AI and appears once one is saved. Three saved words
count as `3 new`. The match is played by keys and by taps, a wrong pair is marked and graded
Again (back in 1 min), and the right ones Good (10 min). A recall judged correct moves to 13
days. Sentence, give-up, and translation from Russian once a language was set from the
definition box all work. On a phone the words wrap across the top. The answer field is not
focused on arrival, so the keyboard does not cover the question.

**Not done.** No real model has judged an answer yet. The prompts are written for glm-5.3 and
gpt-class models but were exercised only against the fake. There is no reader override for a
verdict the reader disagrees with.

## D45 — An admin panel, kept out of the reader's app

**Status:** Adopted · 2026-10-19

**Context.** The site runs unattended on one server. The owner wanted to see failed sign-ins,
who is online, the exceptions and their text, saved words, which novels are being read, and
response times as p50 and p95 graphs. The panel's files were not to be part of the reader's
app, and nothing of it was to load unless an admin opened it.

**Decision.**
- **A second Angular application** in the same workspace (`client/projects/admin`), built by
  `ng build admin` into `WebApi/admin-ui/`, **outside `wwwroot`**. `UseStaticFiles` cannot
  reach it. `AdminController` serves its shell at `/Admin` and its files at `/Admin/{path}`
  behind the `admin` policy, and its JSON at `/Admin/api/overview` and `/Admin/api/timings`.
  The reader's app has no link to it and never loads any of it.
- **Who:** the names under `Admin:Users` (`anton`), case-insensitive. A signed-out visitor is
  sent to sign in (the API answers 401). A signed-in reader who is not an admin gets **404**
  everywhere under `/Admin`, so the panel does not reveal itself. Hashed files are cached as
  `private`.
- **Response times:** a middleware times each HTTP request, named by its endpoint
  (`POST auth/signin`), or as `static files`/`unmatched`. A hub filter times each hub call
  (`hub LoadChapter`), because the reading page does nearly everything over one long-lived
  connection, which the middleware skips. Durations go into per-minute, per-operation
  histograms on a log scale (`LatencyHistogram`, 20%-wide buckets, read back within about
  ±10%). Those merge by addition, so a 30-day graph is a sum of minutes rather than a list of
  requests. The minute under way is in memory. Finished minutes are written every 10 s, and the
  current one at shutdown.
- **Sign-ins:** sign-in, sign-up, lockout, rate-limit refusals and password-change refusals are
  recorded with the name as typed and the client's address. An unknown name and a wrong
  password are one outcome and are recorded alike, so D41's equal timing holds.
- **Errors:** a logging provider queues every warning and error, with the exception's full
  text, and the writer stores them. If the queue overflows the oldest are dropped; the journal
  still has everything.
- **Online:** open hub connections, in memory, with device, address and the last hub call.
- **Readers:** per account, saved words, review cards (due, learning, mature) and every novel
  with its bookmark, read from the existing stores.
- **Storage:** a separate SQLite file, `novelreader-monitoring.db`, beside the accounts
  database. It is pruned hourly: timings and errors after 30 days, sign-in events after 90,
  and at most 2000 errors. Losing it loses nothing a reader would miss, and it never touches
  the accounts file.

**Verified.** 11 unit tests cover the histogram, the report's slots and filters, the hand-off of
finished minutes from memory to the store, two processes writing one minute, and pruning; all
298 pass. Against scratch databases, a browser checked the access rules. Signed out, `/Admin`
redirected to sign-in and the API answered 401. A non-admin got 404 for the page, the API and a
hashed file, and neither reader's page requested anything under `/Admin`. The panel showed the
online reader, 10 wrong passwords, 6 lockouts and 1 rate-limited address (sent with a spoofed
`X-Forwarded-For` from loopback), the lockout warnings with their text, every reader's words
and novels, and timed hub calls. There were no console or CSP errors, and nothing overflowed at
phone width. A graceful stop wrote the minute under way.

**Not done.** No alerting: the panel shows what happened but tells no one. Nothing watches
from outside, so the panel cannot report that the site itself is down.

## D46 — Free answers are graded by Jev first, the reader's AI second

**Status:** Adopted · 2026-10-19

**Context.** D44 sends every free answer (recall, sentence, translation) to the reader's own AI,
which takes a second or two at best and is paid for by the reader. A verdict out of three is a
classification, not writing. TypeSafe's Jev is a decision model built for exactly that, at
about half a second and $0.042 per million input tokens, with output free.

**Decision.**
- `IAnswerJudge` (Domain) picks a verdict and nothing else. `ReviewTutor` asks it first and
  asks the reader's AI only when it returns null.
- `JevAnswerJudge` (`NovelReader.Dictionary`) sends one `choice` question to
  `POST https://api.typesafe.ai/v1/systemone` (model `jev-latest`). The options are
  `correct`/`partial`/`wrong`, each described by the same rules the AI prompts use. The text it
  judges is the same as the AI's question.
- **Null, and so the AI, when:** the call fails or runs past 3 s, the reply is unreadable, or
  the chosen option's probability is under 0.6. A torn verdict is exactly the case where the
  AI's explanation is worth paying for.
- **No feedback text** on a Jev verdict. The page already shows the meaning (or a good
  translation) beside every verdict, and the feedback mostly repeated it.
- **The app's key, not the reader's:** `JEV_ACCESS_KEY` from the environment (`.env`, and the
  server's `EnvironmentFile`). Without it nothing is registered, and every answer goes to the
  AI as before. The review still requires the reader's AI, which writes the glosses and the
  translation sentences.

**Verified.** 9 unit tests cover reading Jev's reply and the confidence cut-off, and the tutor
taking the judge's verdict without asking the AI or falling back when it has none. All 307 pass.
Against the live API, with the requests `JevAnswerJudge` sends, 12 hand-written answers whose
verdict was clear got 12 right: 4 recall, 4 sentence, and 4 translations from Russian. Each
took about 370 ms (one 1.4 s) and about 450 input tokens, roughly $0.00002 an answer. The reply
matched the documented shape, with `probabilities` keyed by option name. Its `confidence` is
lower than the top probability (0.30 against 0.53), so the cut-off reads the probability.
One recall ("a lamp you carry with a candle inside" for *lantern*) was right at 0.53, so it
would go to the AI. That is the torn case the cut-off is for.

**Not done.** The 0.6 cut-off is set from those 12 answers; tune it on real readers' answers.

## D47 — Jev picks the sense in context and checks scraped chapters

**Status:** Adopted · 2026-10-19

**Context.** After D46, two more places in the app make a pick-one decision about text with
nothing smarter than a fixed rule. A reader without their own AI gets the dictionary's senses
in the dictionary's order, so *saw* in "she saw him leave" opens on the tool. And a scraped
page is recognised as junk only when it is the one notice whose wording we know (D21).

**Decision.**
- **One `JevClient`** in `NovelReader.Dictionary`, shared by the three Jev services. It handles
  the HTTP call, logging and reading the reply. `AddJev` replaces `AddJevAnswerJudge` and
  registers nothing without `JEV_ACCESS_KEY`.
- **Sense in context (`ISenseChooser`, `JevSenseChooser`).** One `choice` question per lookup.
  The options are the senses' numbers ("(noun) text", each cut at 300 characters) plus `none`,
  so that an unlisted meaning is not forced onto a wrong one. The paragraph is capped at 2000
  characters as for the AI. A pick under 0.5 keeps the dictionary's order.
  `AssistedDefinitionService` moves the pick to the front, after the cache and only for this
  request: the order depends on the paragraph, so it is never stored.
- **Keep every sense, show twelve.** The first live test failed because the right sense was not
  offered. Wiktionary lists entry after entry, and the old cap of 12 kept only the first: all
  12 of *bank* were about money (the river is 16th of 42), and *saw*, *rose*, *left* and *felt*
  never reached their past-tense senses. The providers now keep up to 64 senses and the box is
  cut to 12 after ranking. Cached definitions carry `format: 2`, and an older entry is fetched
  again on its next lookup. Side effect: `WordForms.BaseFromDictionary` (D39) now also sees the
  late "simple past of …" notes, so a saved *left* (main sense an adjective) gets the base
  *leave*.
- **The page always sends the paragraph with a definition request.** Translations still send
  it only for AI readers, because MyMemory does not use it.
- **Chapter check (`IChapterTextCheck`, `JevChapterTextCheck`).** One yes/no question on the
  first 20 paragraphs (at most 4000 characters) of each fresh scrape, asked in
  `ChapterPreparationService` before the chapter is stored. A page is refused only when the
  probability it is a chapter is under 0.2, by the same exception as the "page moved" notice,
  so nothing is cached and the next load scrapes again. A failed or unsure check lets the page
  through: refusing a real chapter costs the reader the chapter, while caching junk costs a
  cleanup. The wording check of D21 stays in front of it.

**Verified.** 14 new unit tests cover the client's reading of choices and yes/no answers, each
service's thresholds, the reordering and the cut to twelve, and a refused page that is not
stored and is scraped again. All 321 pass. Against the live API, the sense was picked right
for 13 of 13 words with all senses offered: *saw* (see, and the tool), *bank* (river, and
money), *bolt*, *cultivation*, *grave*, *rose*, *left* twice, *felt* and *spring* twice. One
right pick scored 0.58 and the rest 0.61 or more, at about 360 ms and 500–1600 input tokens
each (under $0.0001). Five site pages (the moved notice, a browser check, a paywall, a 404,
"Loading...") scored 0.04 or less as chapters. Three real ones, including an author's
announcement and a four-line chapter, scored 0.83 or more. Through the real DI wiring and
Wiktionary, *bank*, *saw* and *spring* opened on the sense the paragraph used.

**Not done.** The definition box does not say that the order was chosen. Each lookup without an
AI now waits about 350 ms more for the pick, which a cache keyed by (term, paragraph) could save
on a re-read. The thresholds come from hand-made cases, not real pages.

## D48 — A dot spinner while a save or a chapter is on its way

**Status:** Adopted · 2026-10-20

**Context.** Saving a word and loading the next chapter both wait on the server, and nothing
on the page said so. A save showed nothing until the underline appeared, and the bottom of the
page stayed blank until the chapter arrived. On a slow scrape or a cold Mongo that looks like
the press or the scroll did nothing.

**Decision.**
- **`ProgressDots`** (`app/core/progress-dots.ts`) draws `.  ` → `.. ` → `...` → `.  `, 400 ms a
  step, after the words that say what is happening. It is pure CSS (`.progress-dots` in
  `tui.css`): each dot keeps its room and two of them step their opacity, so the text never
  shifts and an idle page runs no timer. With reduced motion all three dots just stand.
- **Saving or deleting in the box:** `s save` becomes `saving...` (and the tap button reads
  `saving...`, disabled) from the press until the hub call settles. `saveWord`/`deleteWord` now
  return that promise. The vocabulary push arrives before the call's own answer, so the dots
  give way to the new state. A failed call clears them, and a second press waits.
- **Saving straight from a selection** (`s` with no box open): a dim `saving...` chip in the
  bottom-left corner, which gives way to `reconnecting…` there.
- **A chapter on its way:** `loading chapter 12...` under the column, set in `#loadAndAppend`
  and cleared *before* the chapter is rendered, so the two are never in the column together
  while it is measured (D36, D37).
- On an empty page (a novel opening, a jump to a chapter) the chapter line sits at 35vh. As the
  first line it was under the fixed corner buttons, and on the phone `☰ navigate` covered the
  chapter number and the dots.
- The chip and the chapter line wait 250 ms before showing, so a prefetched chapter or a quick
  save does not flash them. The box's label does not wait, because it is the answer to the
  press.

**Verified.** The client builds. Stepping the animation's clock in Chromium gave `.  ` at
0–399 ms, `.. ` at 401–799, `...` at 801–1199 and `.  ` again at 1201. In the running app,
`s` on a selection showed the `saving...` chip and cleared it when the hub answered. On a
phone (Galaxy S23+, Firefox for Android, through `adb reverse`, with local Mongo and screenshots
over adb), a jump to chapter 50 showed `loading chapter 50` with the dots stepping, clear of the
corner buttons. Flinging to the end of chapter 51 showed `loading chapter 52.` under the text.
The box's tap button read `saving.` and `saving..`, disabled, while Mongo was paused, and turned
into `delete` once it resumed.

**Not done.** The `translating…` and `loading…` labels keep their static ellipsis. When a chapter
load fails, the line clears and nothing replaces it: chapter 52 failed on the phone with the
source site's empty-200, the page took it for the end of the novel (existing behaviour, see
`ParagraphsRetriever`), and the page shrinking by the line's height moved the text by it.

## D49 — A chapter the site did not serve is retried, not taken for the end

**Status:** Adopted · 2026-10-20

**Context.** On the phone test (D48), chapter 52 failed and the page stopped there as if the
novel had ended, with no message. Every failure reached the page as the same `found: false`
that meant "past the last chapter". Requests to the source site showed two different things
behind it:
- **A random redirect.** About one request in six (measured on chapter 52) is answered with a
  302 to another site's "Loading..." page. The HTTP client followed it and found no paragraphs:
  the long-known "200 with no paragraphs". The next request gets the chapter.
- **The real end.** Past the last chapter the site answers 200 with a page titled "Page Chapter
  Not Found", whose body is the "page moved" notice. D21 took that notice for a notice about
  moved pages; it is the site's not-found page.

**Decision.**
- **Two exceptions** in `Domain/Parsing`, both `InvalidOperationException`s:
  `ChapterNotFoundException` (the site does not have it) and `ChapterUnavailableException` (not
  served this time).
- **`ParagraphsRetriever`** no longer follows redirects (`AllowAutoRedirect = false`). A 3xx, a
  non-success status and a page without the content div are *unavailable*. A 404, the "Chapter
  Not Found" title and D21's notice are *not found*.
- **`ChapterPreparationService` retries** an unavailable chapter, a network error or a timeout
  after 1 s and then 3 s (`RetryDelays`), then gives up. Not found is never retried. A page
  that Jev refuses (D47) now counts as unavailable, because the site's own pages come and go
  like the redirect does.
- **The hub answers** `failure: "unavailable"` for anything that still failed. Not found with no
  failure is the end of the novel, logged as nothing at all.
- **The page:**
  - An unavailable chapter shows `chapter 53 did not load · retry` where the loading line was.
    The button, `r`, or scrolling on at the bottom asks again; after a failed jump the scroll
    retries the jump, never "chapter 1" of an empty page.
  - Rolling off the last chapter shows `no chapter 2335 yet` once, quietly.
  - The three lines share `.chapter-status`, so one replaces another without the text moving.

**Verified.** 8 new unit tests cover the retriever's classification (redirects, errors, 404,
the not-found title) and the retry rules (retried until served, stops after the last wait, not
found asked once). All 329 pass. On the phone (Firefox for Android) the app was run with its
proxy pointed at a closed port: scrolling to the end of chapter 52 showed `loading chapter
53...`, then `chapter 53 did not load · retry`. The server log showed three attempts for the
reader's load and three for the background prefetch. With the app restarted on the real proxy,
the page reconnected by itself, and tapping `retry` showed `loading chapter 53...` and then the
chapter in place of the line. Jumping to chapter 2334 of *Reverend Insanity* and scrolling to
its end showed `no chapter 2335 yet`: the live site's not-found page, asked for once.

**Not done.** A gap in a novel's numbering, a chapter the site never had in the middle, still
reads as the end; the catalogue's chapter count (D22) could tell the two apart. The retry is
by hand after the server's three attempts; nothing retries on a timer.
