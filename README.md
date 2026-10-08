# Novel Reader

A keyboard-driven web reader for serialised novels, with a dictionary built into the page.

Select a word, press `d`, and the definition appears in a panel pointing at it — from a
dictionary, or from an AI of your choosing that reads the word in its paragraph. Save the words
you look up and they stay underlined everywhere you meet them again. The reader remembers
where you stopped — down to the paragraph — and puts it back at the bottom of the screen when
you return.

![The reading view with a definition open](docs/screenshots/reading-definition.png)

## What it does

- **Reads by the chapter.** A whole chapter arrives in one request and is cached in MongoDB,
  so the source site is fetched once per chapter and the next one is warmed in the background
  while you read.
- **Defines words in place.** Wiktionary first, [dictionaryapi.dev](https://dictionaryapi.dev)
  as a fallback, cached by term. Senses page with `j`/`k`.
- **Translates words and phrases** with [MyMemory](https://mymemory.translated.net) into a
  language you pick once. Select a phrase and press `t`.
- **Uses your own AI, if you want it.** In the settings drawer, choose a service
  (DigitalOcean, OpenAI, Anthropic, OpenRouter, DeepSeek, Mistral or Groq), a model and an API
  key. Definitions and translations then come from that model, which is sent the paragraph
  the selection came from, so a word gets the meaning it has *there*. The key is stored
  encrypted and never sent back to the page. If the AI fails, the dictionary answers instead.
- **Lets you set the page up.** The settings drawer (`c`, or the `>` button in the top-left
  corner on a phone) has a font-size slider, a separate chapter-title size slider, and hex fields for the background and text colours. They are
  kept per device and applied before the page first paints. One button resets them all.
- **Remembers your vocabulary.** Saved words are underlined in every chapter, in all their
  forms: save `sneered` and `sneer`, `sneers` and `sneering` are underlined too, as is `ran`
  for a saved `run`. Clicking one reopens the word you saved.
- **Keeps the page short.** At most four chapters are on the page at once: the one you are
  reading, two above it and the next one below. Older chapters are removed as you read on.
- **Holds your place when the screen changes.** Turning the phone, even while it is off, and
  changing the font size both keep you on the paragraph you were reading.
- **Remembers your place.** The last paragraph actually on screen is recorded once you stop
  scrolling — one request, not one per scroll event. Reopening restores that paragraph to the
  bottom of the viewport, loading the previous chapter too when the bookmark sits near the top
  of its own.
- **Keeps a library.** Novels you have read are listed with their title, rank and chapter
  count, refreshed from the catalogue once a day.
- **Searches for new novels**, with results as you stop typing.
- **Accounts.** Sign up, sign in, and a per-reader library and vocabulary.

Everything is driven from the keyboard, and every panel is drawn in the same TUI style. On a
phone the keys become buttons: `☰ navigate` and `>` in the corners, and `d definition` /
`t translate` while text is selected.

## Screenshots

**Sign in / sign up** — two fields, two modes, and failures shown in place rather than by
reloading:

![Sign in](docs/screenshots/login.png)

**The navigation menu** (`n`) — a stack of screens with a breadcrumb, filter and hint bar:

![The navigation menu](docs/screenshots/navigation.png)

**Searching the catalogue.** Results arrive 1.5 s after you stop typing. A title too long for
its row fades out over its last characters rather than being clipped, so the rank and chapter
count keep their place:

![Searching for novels](docs/screenshots/search.png)

## Quick start

```bash
./run.sh
```

That starts MongoDB in a container, restores the SignalR client, builds everything (including
the TypeScript), and opens the app at <http://localhost:5261>. Sign up, and you start reading.

```bash
./run.sh --https     # the https profile instead
./run.sh --stop      # stop the Mongo container
./run.sh --clean     # remove the container and its data volume
./run.sh --help
```

The Mongo container is left running after `Ctrl-C` so cached chapters survive a restart.

### Prerequisites

`run.sh` checks for these and tells you what is missing:

- **.NET 10 SDK**, plus the ASP.NET Core runtime and targeting pack. On Arch:
  `sudo pacman -S aspnet-runtime aspnet-targeting-pack`
- **Node.js** — the TypeScript client is compiled during `dotnet build`.
- **Podman** — for the MongoDB container.

Accounts live in a local SQLite file created on first run. Nothing else needs setting up.

### Running the tests

```bash
dotnet test NovelReader.sln     # 162 tests
cd WebApi && npm run typecheck  # client types
```

## Keyboard

Keys are dispatched through a stack of modes, so the innermost thing on screen gets them first
and the bindings underneath come back untouched when it closes.

**Reading**

| Key | |
|---|---|
| `j` / `k` | scroll down / up |
| `d` | define the selected word |
| `t` | translate the selected phrase |
| `s` | save the selected word |
| `n` | open the navigation menu |
| `c` | open the settings drawer |

**With a definition open**

| Key | |
|---|---|
| `j` / `k` | next / previous sense |
| `s` / `d` | save / delete the word |
| `t` | translate |
| `e` | translation settings (email and language) |
| `Esc` | close |

**In the settings drawer**

| Key | |
|---|---|
| `j` / `k` | previous / next row |
| `h` / `l` | along the row (colour swatch ↔ hex, save ↔ remove); on the font-size row, smaller / larger |
| `Enter` | edit the field under the cursor, or press the button |
| `Esc` | leave the field you are editing, or close the drawer |
| `c` | close |

**In the navigation menu**

| Key | |
|---|---|
| `j` / `k`, arrows | move |
| `g` / `G` | first / last |
| `Enter` | select |
| `/` | filter — or, on the search screen, query the catalogue |
| `Esc` | back one screen, and close at the root |

In the chapter list you can type **any** chapter number, including one outside the range shown,
and it will try to fetch it.

## How it is built

Seven projects, with dependencies pointing inward at `NovelReader.Domain`, which has **zero
package references** and holds the interfaces, POCOs and pure logic.

| Project | |
|---|---|
| `NovelReader.Domain` | contracts and business logic — reading, vocabulary, accounts, the library |
| `NovelReader.Retrievers` | scrapes chapters and queries the catalogue |
| `NovelReader.Dictionary` | dictionary providers with fallback, MyMemory translation, and the OpenAI-compatible client for the reader's AI |
| `NovelReader.Data.Mongo` | chapters, vocabulary, bookmarks, prepared-chapter cache |
| `NovelReader.Data.Sqlite` | user accounts, translation settings, AI settings |
| `WebApi` | ASP.NET Core host, SignalR hub, and the TypeScript client |
| `NovelReader.Tests` | xUnit |

The frontend is TypeScript with **no framework and no bundler** — `tsc` emits browser-native
ES modules straight into `wwwroot/`. The four panels (definition box, navigation menu, settings
drawer, login) share one palette in `wwwroot/tui.css`. The settings drawer is a list of
independent sections, so adding an option means adding a section. The drawer starts scrolling
once its sections no longer fit the screen.

Chapters are stored one document per chapter, per novel; bookmarks one document per
(reader, novel). Passwords are hashed with PBKDF2-HMAC-SHA256, and the hub takes the reader
from the authentication cookie rather than from anything the client sends. API keys are
encrypted with ASP.NET Core Data Protection. Its key ring sits in `novelreader-keys/` next to
the accounts database; **back the two up together**, because the stored keys cannot be read
without the ring. The AI services are a fixed list, never a URL the reader types, so the
server can only be pointed at those services.

## Status and limitations

This is a working personal reader, not a finished product. Worth knowing:

- **Translations are not cached**, unlike definitions. AI answers are deliberately never
  cached, because each one is about a word in one particular paragraph.
- **AI definitions are slow.** With glm-5.3 they take about 5–10 s, so the box usually says
  `still looking…` before the answer arrives.
- **AI translation still needs the translation settings** (`e`) to know the target language, and
  that form asks for an email the AI does not use.
- **Chapter counts are not known per novel**, so the chapter menu shows a window around where
  you are and "next chapter" discovers the end of a novel by trying it.
- **Accounts have no password reset**, no password change, and no rate limit on sign-in.
- **Scraping is fragile by nature.** The source site occasionally answers `200` with an empty
  page, and its "this page moved" notice is recognised by its wording — if that wording
  changes, so must the code.
- Progressive-web-app support is deliberately deferred.

## The documentation

- [`CLAUDE.md`](CLAUDE.md) — how the codebase fits together, and the traps worth knowing before
  changing it.
- [`DECISIONS.md`](DECISIONS.md) — why things are the way they are, one decision per entry.

## A note on the source

The reader fetches chapters from a public novel site for personal reading, caching each chapter
once so the site is not fetched repeatedly. It is not a scraper for redistribution, and no
chapter text is included in this repository — the prose in the screenshots above was written
for them.
