import {
  NavigationMenu,
  type MenuItem,
  type MenuScreen,
} from "./interactive-select/module.js";
import { KeyboardRouter, bindings, type KeyMode } from "./input/keyboardRouter.js";
import { isTouchPrimary, markPointerMode } from "./input/pointer.js";
import {
  ReaderConnection,
  type ChapterView,
  type ConnectionState,
  type NovelSummary,
  type ReadingSessionView,
  type TranslationLanguageView,
} from "./reading/connection.js";
import { parseChapterNumber } from "./reading/chapterNumber.js";
import { ProgressReporter } from "./reading/progressReporter.js";
import { SmoothScroller } from "./reading/scroller.js";
import {
  readSelection,
  clearSelection,
  rectOf,
  rectOfRange,
  type SelectedTerm,
} from "./reading/selection.js";
import { underlineTerm, removeUnderline } from "./reading/underliner.js";
import { DefinitionBox } from "./ui/definitionBox.js";

/**
 * The reader is whoever the auth cookie says; the page never names them (D20). Everything
 * else — which novel, which chapter, which paragraph — arrives in the reading session.
 */

// ---- State ----

let novelName = "";
let novels: readonly NovelSummary[] = [];

/** The highest chapter currently on the page; the next scroll past the bottom loads its successor. */
let lastLoadedChapter = 0;
let loadingChapter = false;
/** Set once a chapter comes back empty, so the page stops walking off the end of the novel. */
let reachedEnd = false;

const paragraphsContainer = document.getElementById("novel-paragraphs");
const connectionStatus = document.getElementById("connection-status");
const definitionAffordance = document.getElementById("definition-affordance");
const translateAffordance = document.getElementById("translate-affordance");

// ---- Pieces ----

const router = new KeyboardRouter();
const scroller = new SmoothScroller();
const definitionBox = new DefinitionBox();

const navigationHost = document.getElementById("navigation-container");
const navigationAffordance = document.getElementById("navigation-affordance") ?? undefined;
const navigationMenu = navigationHost
  ? new NavigationMenu(navigationHost, navigationAffordance)
  : undefined;

const progressReporter = paragraphsContainer
  ? new ProgressReporter(paragraphsContainer, (position) => {
    connection.reportProgress(novelName, position.chapterNumber, position.paragraphNumber);
  })
  : undefined;

/**
 * The settled selection the touch "d definition" button will look up. Captured when the
 * button appears, so the button can be tapped without the browser's own handling of that tap
 * having to leave the selection alone (D26).
 */
let pendingSelection: SelectedTerm | undefined;

/** Re-run once the connection is back, when a chapter load was cut short by losing it (D28). */
let retryWhenConnected: (() => void) | undefined;

/**
 * The reader's translation settings, as the server last told us. Null email means they have
 * never set translation up, which is what makes `t` open the form instead of asking for a
 * translation nobody can fetch (D31).
 */
let translationEmail: string | null = null;
let translationLanguage: string | null = null;
let translationLanguages: readonly TranslationLanguageView[] = [];

const definitionMode: KeyMode = {
  name: "definition",
  onEnter: () => scroller.lock(),
  onExit: () => scroller.unlock(),
  bindings: bindings({
    j: { description: "next sense", run: () => definitionBox.nextSense() },
    k: { description: "previous sense", run: () => definitionBox.previousSense() },
    s: { description: "save word", run: () => definitionBox.save() },
    d: { description: "delete word", run: () => definitionBox.deleteTerm() },
    t: { description: "translate", run: () => definitionBox.translate() },
    e: { description: "translation settings", run: () => definitionBox.editSettings() },
    Escape: { description: "close", run: () => closeDefinitionBox() },
  }),
};

const navigationMode: KeyMode = {
  name: "navigation",
  onEnter: () => scroller.lock(),
  onExit: () => scroller.unlock(),
  bindings: bindings({
    j: { description: "next option", run: () => navigationMenu?.move(1) },
    k: { description: "previous option", run: () => navigationMenu?.move(-1) },
    ArrowDown: { description: "next option", run: () => navigationMenu?.move(1) },
    ArrowUp: { description: "previous option", run: () => navigationMenu?.move(-1) },
    g: { description: "first option", run: () => navigationMenu?.moveToFirst() },
    G: { description: "last option", run: () => navigationMenu?.moveToLast() },
    Enter: { description: "select", run: () => navigationMenu?.activate() },
    // `?` is what the previous menu used to reach the filter; `/` is the TUI habit.
    "/": { description: "filter", run: () => navigationMenu?.focusFilter() },
    "?": { description: "filter", run: () => navigationMenu?.focusFilter() },
    Escape: { description: "back", run: () => navigationMenu?.back() },
  }),
};

const readingMode: KeyMode = {
  name: "reading",
  bindings: bindings({
    j: { description: "scroll down", run: () => scroller.start("down") },
    k: { description: "scroll up", run: () => scroller.start("up") },
    d: { description: "define selection", run: () => defineSelection() },
    t: { description: "translate selection", run: () => translateSelection() },
    s: { description: "save selection", run: () => saveSelection() },
    n: { description: "navigation", run: () => openNavigation() },
  }),
};

// ---- Definition flow ----

function defineSelection(): void {
  const selected = readSelection();
  if (!selected) return;

  openBoxFor(selected.text, selected.rect);
}

/**
 * Opens the box on the word straight away and asks for its definition. The box shows
 * `loading…` until the answer lands — and says so if none does — so waiting never looks the
 * same as nothing happening (D27).
 */
function openBoxFor(term: string, anchor: DOMRect): void {
  hideSelectionAffordances();

  definitionBox.open(term, anchor, {
    onSave: (saved) => connection.saveWord(novelName, saved),
    onDelete: (saved) => connection.deleteWord(saved),
    onTranslate: (saved) => translateOrConfigure(saved),
    onEditSettings: () => openTranslationSettings(),
    onClose: () => closeDefinitionBox(),
  });

  clearSelection();
  if (!router.has("definition")) router.push(definitionMode);

  connection.requestDefinition(term);
}

/**
 * `t` on a selection of more than one word, and the phone's `t translate` button.
 *
 * The definition is asked for at the same moment and shown in the same box, but nothing waits
 * on it: a phrase is selected because the reader wants it translated, and the translation is
 * rendered the moment it lands, above a definition that is still loading (D32).
 */
function translateSelected(selected: SelectedTerm): void {
  // One word already has `d`, and its translation lives inside that box.
  if (selected.wordCount < 2) return;

  openBoxFor(selected.text, rectOfRange(selected.range) ?? selected.rect);
  translateOrConfigure(selected.text);
}

function translateSelection(): void {
  const selected = readSelection();
  if (selected) translateSelected(selected);
}

/**
 * `t`. A reader who has not set translation up is asked to, rather than being sent to a
 * server that can only answer "not configured" (D31) — the server still refuses that case,
 * but the reader should not need a round trip to be told what to do.
 */
function translateOrConfigure(term: string): void {
  if (translationEmail === null || translationLanguage === null) {
    openTranslationSettings();
    return;
  }

  definitionBox.showTranslationPending();
  connection.requestTranslation(term);
}

/** `e`, and the toolbar's settings button. Prefilled when there is something to edit. */
function openTranslationSettings(): void {
  if (!definitionBox.isOpen) return;

  definitionBox.openSettings(
    { email: translationEmail, language: translationLanguage, languages: translationLanguages },
    {
      onSubmit: (email, language) => void applyTranslationSettings(email, language),
      onCancel: () => definitionBox.cancelSettings(),
    },
  );
}

/**
 * Stores the settings and asks for the translation in the same breath.
 *
 * The translation carries the settings with it because the two calls race: the save may not
 * have landed when the translation is read, and the reader should not have to press `t` twice
 * on the first word they ever look up (D31). Every later request leaves them out.
 */
async function applyTranslationSettings(email: string, language: string): Promise<void> {
  const asked = definitionBox.surfaceForm;

  definitionBox.cancelSettings();
  if (asked !== undefined) {
    definitionBox.showTranslationPending();
    connection.requestTranslation(asked, { email, language });
  }

  const result = await connection.saveTranslationSettings(email, language);

  if (result.error !== null) {
    // The server refused what the page accepted. Put the form back with the reason.
    openTranslationSettings();
    definitionBox.showSettingsError(
      result.error === "email-invalid"
        ? "The server would not accept that email address."
        : "The server would not accept that language.",
      result.error === "email-invalid" ? "email" : "language",
    );
    return;
  }

  translationEmail = result.email;
  translationLanguage = result.language;
}

function saveSelection(): void {
  const selected = readSelection();
  if (!selected) return;

  connection.saveWord(novelName, selected.text);
}

function closeDefinitionBox(): void {
  definitionBox.close();
  router.pop("definition");
}

// ---- Connection ----

const connection = new ReaderConnection({
  onDefinition: (view) => {
    // The box is already open on the word that was asked for; an answer for anything else is
    // one the reader has moved on from.
    if (!definitionBox.isOpen || definitionBox.surfaceForm !== view.surfaceForm) return;

    definitionBox.showDefinition(view);
  },

  onConnectionStateChanged: (state) => showConnectionState(state),

  onTranslation: (surfaceForm, translation) => {
    // A late answer for something the reader has already moved on from is dropped. Matched on
    // the surface form, because the definition may not have arrived to name a term yet (D32).
    if (!definitionBox.isOpen || definitionBox.surfaceForm !== surfaceForm) return;

    // The server is the backstop for settings the page thought were there and were not.
    if (translation.error === "not-configured") {
      openTranslationSettings();
      return;
    }

    definitionBox.showTranslation({
      text: translation.text,
      note: translation.note,
      error: translation.error === null ? null : describeTranslationFailure(translation.error),
    });
  },

  onVocabularyChanged: (term, isSaved) => {
    if (definitionBox.isOpen && definitionBox.term === term) {
      definitionBox.setSaved(isSaved);
    }

    if (!paragraphsContainer) return;
    if (isSaved) {
      underlineTerm(paragraphsContainer, term);
    } else {
      removeUnderline(paragraphsContainer, term);
    }
  },
});

/** Failure codes are the server's; the wording is the page's. */
function describeTranslationFailure(code: string): string {
  switch (code) {
    case "unavailable":
      return "translation unavailable — the service may be out of allowance for today";
    case "settings-invalid":
      return "check your translation settings (e)";
    default:
      return "translation failed";
  }
}

// ---- Rendering ----

/** Paragraph 1 of a chapter is its title. */
function isChapterTitle(paragraphNumber: number): boolean {
  return paragraphNumber === 1;
}

/**
 * A paragraph made only of separator characters — the source site marks a scene change with a
 * long run of dashes. Recognised so it can be drawn as a rule rather than read as text (D34).
 */
const sceneBreakPattern = /^[-–—_=~*·•\s]{4,}$/;

function appendChapter(chapter: ChapterView): void {
  if (!paragraphsContainer) return;

  for (const paragraph of chapter.paragraphs) {
    const element = document.createElement("p");
    if (isChapterTitle(paragraph.number)) {
      element.className = "data-chapter-title";
    }

    // The bookmark is read back off these, so every paragraph says where it is.
    element.dataset["chapter"] = String(chapter.chapterNumber);
    element.dataset["paragraph"] = String(paragraph.number);

    // The server escapes every scraped character and emits only its own tags (D3), so this
    // is markup the server authored, not text the novel site controls.
    element.innerHTML = paragraph.markup;

    if (sceneBreakPattern.test(element.textContent ?? "")) {
      element.classList.add("scene-break");
    }

    paragraphsContainer.appendChild(element);
  }
}

async function loadAndAppend(chapterNumber: number): Promise<ChapterView> {
  const chapter = await connection.loadChapter(novelName, chapterNumber);

  if (chapter.found) {
    appendChapter(chapter);
    lastLoadedChapter = Math.max(lastLoadedChapter, chapterNumber);
  } else if (!chapter.failed) {
    // An empty chapter is the end of the novel. A call that never got through is not: the
    // page waits for the connection instead of deciding the novel has run out (D28).
    reachedEnd = true;
  }

  return chapter;
}

/**
 * Waits for the reading column to stop moving.
 *
 * The column is set in a web font. Until it loads, every paragraph is laid out at the
 * fallback font's metrics and then shifts when it swaps in — so a bookmark restored before
 * that happens is measured against a layout that no longer exists, and the paragraph ends up
 * off screen. Costs nothing once the font is cached.
 */
async function waitForStableLayout(): Promise<void> {
  try {
    await document.fonts?.ready;
  } catch {
    // No font-loading API: the frame wait below is the best that can be done.
  }

  // One frame for the reflow to land — but raced with a timer, because a background tab
  // never paints and `requestAnimationFrame` there simply never fires. Waiting on it alone
  // would hang the open forever and leave the chapter loader wedged (D23).
  await Promise.race([
    new Promise<void>((resolve) => {
      requestAnimationFrame(() => resolve());
    }),
    new Promise<void>((resolve) => {
      setTimeout(resolve, frameWaitFallbackInMilliseconds);
    }),
  ]);
}

/**
 * Puts a paragraph at the bottom of the viewport, which is where it was when the reader
 * stopped looking at it.
 */
function scrollParagraphToBottom(chapterNumber: number, paragraphNumber: number): boolean {
  const element = paragraphsContainer?.querySelector<HTMLElement>(
    `[data-chapter="${chapterNumber}"][data-paragraph="${paragraphNumber}"]`,
  );
  if (!element) return false;

  const rect = element.getBoundingClientRect();
  window.scrollTo({ top: window.scrollY + rect.bottom - window.innerHeight });
  return true;
}

/**
 * How close to the top of a chapter a bookmark has to be before the previous chapter is
 * loaded as well. Restoring to paragraph 3 with nothing above it looks like a bug.
 */
const nearTopOfChapter = 10;

/** How long to wait for a paint that a hidden tab will never make. */
const frameWaitFallbackInMilliseconds = 100;

/**
 * Opens a novel at a position, loading one chapter — or two, when the bookmark sits near the
 * top of its chapter and there would otherwise be nothing above it.
 */
async function openNovelAt(
  novel: string,
  chapterNumber: number,
  paragraphNumber: number,
  resuming: boolean,
): Promise<void> {
  if (!paragraphsContainer || loadingChapter) return;

  loadingChapter = true;
  try {
    paragraphsContainer.replaceChildren();
    novelName = novel;
    lastLoadedChapter = 0;
    reachedEnd = false;

    if (resuming && paragraphNumber <= nearTopOfChapter && chapterNumber > 1) {
      await loadAndAppend(chapterNumber - 1);
      // A missing predecessor is not a reason to stop; the wanted chapter still loads.
      reachedEnd = false;
    }

    const chapter = await loadAndAppend(chapterNumber);
    if (!chapter.found) {
      if (chapter.failed) {
        retryWhenConnected = () => void openNovelAt(novel, chapterNumber, paragraphNumber, resuming);
        return;
      }

      showNotice(`Chapter ${chapterNumber} of “${novel}” could not be loaded.`);
      return;
    }

    if (resuming) {
      // Must not be scrolled before the column has settled, or the bookmark lands elsewhere.
      await waitForStableLayout();

      if (scrollParagraphToBottom(chapterNumber, paragraphNumber)) {
        progressReporter?.markReported({ chapterNumber, paragraphNumber });
        return;
      }
    }

    window.scrollTo({ top: 0 });
  } finally {
    loadingChapter = false;
  }
}

function showNotice(text: string): void {
  if (!paragraphsContainer) return;

  const notice = document.createElement("p");
  notice.className = "reading-notice";
  notice.textContent = text;
  paragraphsContainer.appendChild(notice);
}

// ---- Already-underlined words are clickable ----

paragraphsContainer?.addEventListener("click", (event) => {
  const target = event.target;
  if (!(target instanceof HTMLElement)) return;

  const knownWord = target.closest<HTMLElement>(".known-word");
  if (!knownWord) return;

  const term = knownWord.dataset["term"];
  if (!term) return;

  openBoxFor(term, rectOf(knownWord));
});

// ---- Touch input ----

/**
 * How long a text selection must hold still before the definition button offers it. Long
 * enough that dragging the selection handles across a phrase does not flicker the button.
 */
const touchSelectionSettleMs = 450;

/** The selection, but only while it is inside the reading column. */
function selectionInsideColumn(container: HTMLElement): SelectedTerm | undefined {
  const anchor = window.getSelection()?.anchorNode;
  if (!anchor || !container.contains(anchor)) return undefined;

  return readSelection();
}

function hideSelectionAffordances(): void {
  pendingSelection = undefined;
  if (definitionAffordance) definitionAffordance.hidden = true;
  if (translateAffordance) translateAffordance.hidden = true;
}

/**
 * On a touch device there are no `d` and `t` keys, so a settled selection offers buttons
 * instead, stacked under the navigation hint in the top-right corner: `d definition` always,
 * and `t translate` only when more than one word is selected (D26, D32). They are tapped when
 * the reader wants them, not the moment the selection settles.
 *
 * `selectionchange` fires throughout a drag, so the buttons wait for it to stop, and appear
 * only for a selection inside the reading column — never one in the menu filter — with no
 * definition box already open.
 */
function wireTouchSelectionAffordances(
  container: HTMLElement,
  defineButton: HTMLElement,
  translateButton: HTMLElement | null,
): void {
  let settleTimer: number | undefined;

  document.addEventListener("selectionchange", () => {
    if (settleTimer !== undefined) clearTimeout(settleTimer);

    settleTimer = window.setTimeout(() => {
      settleTimer = undefined;

      const selected = definitionBox.isOpen ? undefined : selectionInsideColumn(container);
      pendingSelection = selected;
      defineButton.hidden = selected === undefined;

      // A single word is already covered by the definition and the `t` inside its box; the
      // button is for the phrase case, where there is no definition to hang it off.
      if (translateButton) {
        translateButton.hidden = selected === undefined || selected.wordCount < 2;
      }
    }, touchSelectionSettleMs);
  });

  // Measured at the tap rather than when the button appeared: the page may have been scrolled
  // in between, and the box has to point at where the words are.
  defineButton.addEventListener("click", () => {
    const selected = pendingSelection;
    if (!selected) return;

    openBoxFor(selected.text, rectOfRange(selected.range) ?? selected.rect);
  });

  translateButton?.addEventListener("click", () => {
    const selected = pendingSelection;
    if (selected) translateSelected(selected);
  });
}

if (navigationAffordance) {
  // The hint is also the control: tapping it opens the menu that `n` opens. On a keyboard
  // it stays a decorative hint (pointer-events are off in CSS, so this never fires); on
  // touch it becomes a real, focusable button.
  navigationAffordance.addEventListener("click", () => openNavigation());
  if (isTouchPrimary) {
    navigationAffordance.removeAttribute("aria-hidden");
    navigationAffordance.removeAttribute("tabindex");
  }
}

if (isTouchPrimary && paragraphsContainer && definitionAffordance) {
  wireTouchSelectionAffordances(paragraphsContainer, definitionAffordance, translateAffordance);
}

// ---- Navigation menu ----

/**
 * How far either side of the current chapter the chapter list reaches. There is no endpoint
 * that reports how many chapters a novel has, so the list is a window around where the
 * reader is rather than the whole book.
 */
const chapterWindow = 12;

/** Matches the hub's own minimum; below this the server answers with nothing anyway. */
const minimumSearchLength = 2;

function rootScreen(): MenuScreen {
  return {
    title: "navigation",
    filterPlaceholder: "filter options…",
    items: [
      {
        label: "Go to chapter",
        detail: `reading ${lastLoadedChapter}`,
        run: () => navigationMenu?.push(chapterScreen()),
      },
      {
        label: "Novels you've read",
        detail: novels.length > 0 ? `${novels.length}` : "none yet",
        run: () => navigationMenu?.push(libraryScreen()),
      },
      {
        label: "Search new novels",
        detail: "novelfire",
        run: () => navigationMenu?.push(searchScreen()),
      },
      {
        label: "Sign out",
        run: () => {
          window.location.href = "/auth/signout";
        },
      },
    ],
  };
}

function chapterScreen(): MenuScreen {
  const current = lastLoadedChapter;
  const first = Math.max(1, current - chapterWindow);
  const last = current + chapterWindow;

  const items: MenuItem[] = [];
  for (let candidate = first; candidate <= last; candidate += 1) {
    const target = candidate;
    items.push({
      label: `Chapter ${target}`,
      detail: target === current ? "current" : undefined,
      run: () => {
        navigationMenu?.close();
        void openNovelAt(novelName, target, 1, false);
      },
    });
  }

  return {
    title: "chapters",
    items,
    filterPlaceholder: "chapter number…",
    initialIndex: current - first,
    // The list only spans a window around where the reader is, because nothing reports how
    // many chapters a novel has. Typing a number outside it offers to go there anyway —
    // whether it exists is settled by trying, and a chapter that will not load says so.
    itemsFromFilter: (text) => {
      const wanted = parseChapterNumber(text);
      if (wanted === undefined || (wanted >= first && wanted <= last)) return [];

      return [
        {
          label: `Chapter ${wanted}`,
          detail: "not in the list — try it",
          run: () => {
            navigationMenu?.close();
            void openNovelAt(novelName, wanted, 1, false);
          },
        },
      ];
    },
  };
}


/**
 * The novels the server said this reader has open, shown the same way search hits are. A
 * novel the catalogue has not been asked about yet falls back to its slug and carries no
 * figures — it gets both once the daily refresh has run (D22).
 */
function libraryScreen(): MenuScreen {
  const items: MenuItem[] = novels.map((novel) => ({
    label: novel.title ?? novel.slug,
    detail: novel.slug === novelName ? "reading" : describeNovel(novel),
    run: () => {
      navigationMenu?.close();
      void resumeNovel(novel.slug);
    },
  }));

  return {
    title: "library",
    items,
    emptyMessage: "No novels yet — the ones you read will show up here.",
    filterPlaceholder: "filter novels…",
  };
}

/**
 * English ordinal for a rank: 1st, 2nd, 3rd, 4th… The teens are the exception — 11th, 12th
 * and 13th, not 11st.
 */
function ordinal(value: number): string {
  const lastTwo = Math.abs(value) % 100;
  if (lastTwo >= 11 && lastTwo <= 13) return `${value}th`;

  switch (Math.abs(value) % 10) {
    case 1:
      return `${value}st`;
    case 2:
      return `${value}nd`;
    case 3:
      return `${value}rd`;
    default:
      return `${value}th`;
  }
}

/**
 * The dim right-hand column: rank and length, whichever the catalogue gave. Shared by search
 * hits and library rows, which is why it takes only the two fields it reads.
 */
function describeNovel(novel: { rank: number | null; totalChapters: number | null }): string | undefined {
  const parts: string[] = [];
  if (novel.rank !== null) parts.push(ordinal(novel.rank));
  if (novel.totalChapters !== null) parts.push(`${novel.totalChapters}ch`);

  return parts.length > 0 ? parts.join(" ") : undefined;
}

/**
 * Novel search. The filter field is the query: the menu sends it once the reader has stopped
 * typing, and the rows are whatever came back.
 */
function searchScreen(): MenuScreen {
  return {
    title: "search",
    items: [],
    emptyMessage: "Enter the name of the novel or part of it.",
    filterPlaceholder: "search novels…",
    onQuery: (query) => void runSearch(query),
  };
}

/** Rising number, so a slow answer to an old query cannot overwrite a newer one. */
let searchGeneration = 0;

async function runSearch(query: string): Promise<void> {
  const trimmed = query.trim();
  const generation = ++searchGeneration;

  if (trimmed.length < minimumSearchLength) {
    navigationMenu?.setItems([], "Enter the name of the novel or part of it.");
    return;
  }

  navigationMenu?.setItems([], `Searching for “${trimmed}”…`);

  const results = await connection.searchNovels(trimmed);

  // A slower earlier search must not land on top of a later one.
  if (generation !== searchGeneration) return;

  const items: MenuItem[] = results.map((novel) => ({
    label: novel.title,
    detail: describeNovel(novel),
    run: () => {
      navigationMenu?.close();
      void resumeNovel(novel.slug);
    },
  }));

  navigationMenu?.setItems(items, `Nothing found for “${trimmed}”.`);
}

/** Opens another novel where that novel was left, not where the current one was. */
async function resumeNovel(novel: string): Promise<void> {
  const progress = await connection.getNovelProgress(novel);
  await openNovelAt(novel, progress.chapterNumber, progress.paragraphNumber, progress.resuming);
}

function openNavigation(): void {
  if (!navigationMenu || navigationMenu.isOpen) return;

  // The menu covers the corner the affordances sit in.
  hideSelectionAffordances();

  // The mode goes on first so that a menu closed during `open` still pops cleanly.
  if (!router.has("navigation")) router.push(navigationMode);
  navigationMenu.open(rootScreen(), () => router.pop("navigation"));
}

// ---- Connection state ----

/**
 * The one visible sign that the link to the server is down. It appears while the client is
 * reconnecting — which it does once a second, for as long as it takes — and takes anything
 * that was cut short with it when the connection returns (D28).
 */
function showConnectionState(state: ConnectionState): void {
  // The box says "still looking…" or "connection lost" depending on this (D33).
  definitionBox.setConnectionUp(state === "connected");

  if (connectionStatus) {
    connectionStatus.textContent = state === "connected" ? "" : "reconnecting…";
    connectionStatus.hidden = state === "connected";
  }

  if (state !== "connected") return;

  const retry = retryWhenConnected;
  retryWhenConnected = undefined;
  retry?.();
}

// ---- Start ----

markPointerMode();

router.push(readingMode);
router.start();

connection
  .start()
  .then(async () => {
    const session: ReadingSessionView = await connection.getReadingSession();
    novels = session.novels;
    translationEmail = session.translationEmail;
    translationLanguage = session.translationLanguage;
    translationLanguages = session.translationLanguages;

    await openNovelAt(session.novelName, session.chapterNumber, session.paragraphNumber, session.resuming);
  })
  .catch((error: unknown) => {
    // `start` waits out an unreachable server rather than failing, so reaching here means the
    // first calls after it did not go through. The connection keeps trying underneath.
    console.error(error instanceof Error ? error.toString() : String(error));
    showNotice("Could not reach the server.");
  });

window.addEventListener("scroll", () => {
  // Report where the reader is, once they stop.
  progressReporter?.scheduleReport();

  if (scroller.isLocked || loadingChapter || reachedEnd) return;

  const reachedBottom = window.innerHeight + window.scrollY >= document.body.scrollHeight - 100;
  if (!reachedBottom) return;

  loadNextChapter(lastLoadedChapter + 1);
});

/**
 * Rolls the novel forward by one chapter. A load the connection cut short is asked for again
 * as soon as the connection is back — the reader is at the bottom of the page, so there is no
 * further scroll event coming to try again with (D28).
 */
function loadNextChapter(chapterNumber: number): void {
  if (loadingChapter || reachedEnd) return;

  loadingChapter = true;
  void loadAndAppend(chapterNumber)
    .then((chapter) => {
      if (chapter.failed) retryWhenConnected = () => loadNextChapter(chapterNumber);
    })
    .finally(() => {
      loadingChapter = false;
    });
}
