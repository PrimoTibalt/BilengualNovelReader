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

---

## D5 — No JavaScript/TypeScript runtime libraries

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

## D10 — Translation (`t`) answers with a server-side stub

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

## D25 — Cache-busting is a versioned path prefix, because the client is an ES-module graph

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
