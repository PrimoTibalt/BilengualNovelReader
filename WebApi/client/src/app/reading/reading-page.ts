import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  DOCUMENT,
  DestroyRef,
  type ElementRef,
  type AfterViewInit,
  effect,
  inject,
  signal,
  viewChild,
} from "@angular/core";
import { takeUntilDestroyed } from "@angular/core/rxjs-interop";
import { appUrl } from "../core/app-base";
import type { ChapterView, ConnectionState, NovelSummary } from "../core/hub-models";
import { ProgressDots } from "../core/progress-dots";
import { KeyboardRouter, type KeyMode, bindings } from "../core/keyboard-router";
import { isTouchPrimary, markPointerMode } from "../core/pointer";
import { ReaderConnection } from "../core/reader-connection";
import { ReaderState } from "../core/reader-state";
import { DefinitionBox } from "../definition/definition-box";
import type { MenuItem, MenuScreen } from "../navigation/menu-screen";
import { NavigationMenu } from "../navigation/navigation-menu";
import { ReviewOverlay } from "../review/review-overlay";
import { ReviewSection } from "../review/review-section";
import { AppearanceSection } from "../settings/appearance-section";
import { AssistantSection } from "../settings/assistant-section";
import { PasswordSection } from "../settings/password-section";
import { SettingsPanel } from "../settings/settings-panel";
import { parseChapterNumber } from "./chapter-number";
import { ProgressReporter, type ReadingPosition } from "./progress-reporter";
import { ReadingAnchor } from "./reading-anchor";
import { SmoothScroller } from "./scroller";
import { type SelectedTerm, clearSelection, readSelection, rectOf, rectOfRange } from "./selection";
import { ServerMarkup } from "./server-markup";
import { removeUnderline, underlineTerm } from "./underliner";

/**
 * The reading page. The reader is whoever the auth cookie says; the page never names them
 * (D20). Everything else — which novel, which chapter, which paragraph — arrives in the reading
 * session.
 */

/** One thing in the reading column: a paragraph, or a line the page has to say itself. */
type Block =
  | { readonly kind: "paragraph"; readonly key: string; readonly chapter: number; readonly number: number; readonly markup: string }
  | { readonly kind: "notice"; readonly key: string; readonly text: string };

/**
 * How many whole chapters are kept above the one being read. Enough to scroll back over a
 * scene just finished; not so many that finding a word you saved means scrolling through a
 * book's worth of page (D37).
 */
const chaptersKeptAbove = 2;

/**
 * The most chapters the page holds at once: the one being read, those kept above it, and the
 * one already loaded below it.
 */
const maximumChaptersOnPage = chaptersKeptAbove + 2;

/**
 * How close to the top of a chapter a bookmark has to be before the previous chapter is
 * loaded as well. Restoring to paragraph 3 with nothing above it looks like a bug.
 */
const nearTopOfChapter = 10;

/** How long to wait for a paint that a hidden tab will never make. */
const frameWaitFallbackInMilliseconds = 100;

/**
 * How long a text selection must hold still before the definition button offers it. Long
 * enough that dragging the selection handles across a phrase does not flicker the button.
 */
const touchSelectionSettleMs = 450;

/**
 * How far either side of the current chapter the chapter list reaches. There is no endpoint
 * that reports how many chapters a novel has, so the list is a window around where the
 * reader is rather than the whole book.
 */
const chapterWindow = 12;

/** Matches the hub's own minimum; below this the server answers with nothing anyway. */
const minimumSearchLength = 2;

/** How long after the last font-size change the run counts as over, and is checked again. */
const fontChangeSettleInMilliseconds = 200;

@Component({
  selector: "app-reading-page",
  imports: [
    AppearanceSection,
    ProgressDots,
    AssistantSection,
    DefinitionBox,
    NavigationMenu,
    PasswordSection,
    ReviewOverlay,
    ReviewSection,
    ServerMarkup,
    SettingsPanel,
  ],
  templateUrl: "./reading-page.html",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReadingPage implements AfterViewInit {
  readonly #document = inject(DOCUMENT);
  readonly #changeDetector = inject(ChangeDetectorRef);
  readonly #destroyRef = inject(DestroyRef);
  readonly #router = inject(KeyboardRouter);
  readonly #connection = inject(ReaderConnection);
  readonly #state = inject(ReaderState);

  protected readonly isTouch = isTouchPrimary;

  // ---- State ----

  protected readonly blocks = signal<readonly Block[]>([]);
  #noticeCount = 0;

  #novelName = "";
  #novels: readonly NovelSummary[] = [];

  /** The highest chapter currently on the page; the next scroll past the bottom loads its successor. */
  #lastLoadedChapter = 0;
  #loadingChapter = false;
  /** Set once a chapter comes back empty, so the page stops walking off the end of the novel. */
  #reachedEnd = false;

  /** What `retry` does: the load that came back unavailable, asked for again (D49). */
  #retryUnavailable: (() => void) | undefined;

  /** Re-run once the connection is back, when a chapter load was cut short by losing it (D28). */
  #retryWhenConnected: (() => void) | undefined;

  /**
   * The settled selection the touch "d definition" button will look up. Captured when the
   * button appears, so the button can be tapped without the browser's own handling of that tap
   * having to leave the selection alone (D26).
   */
  #pendingSelection: SelectedTerm | undefined;
  /** The chapter on its way, shown under the column as `loading chapter 12...` (D48). */
  protected readonly chapterInFlight = signal<number | undefined>(undefined);
  /** A chapter the site did not serve, shown with a retry where it would have been (D49). */
  protected readonly chapterUnavailable = signal<number | undefined>(undefined);
  /** The chapter after the last one published, once the reader has reached it (D49). */
  protected readonly noChapterYet = signal<number | undefined>(undefined);
  /** A word saved straight from a selection, with no box open to say so, until the server answers (D48). */
  protected readonly savingSelection = signal<string | undefined>(undefined);
  protected readonly defineAffordanceShown = signal(false);
  protected readonly translateAffordanceShown = signal(false);

  /** The paragraph the open definition box was opened from — kept for a later `t` in that box. */
  #boxParagraph: string | null = null;

  /** Rising number, so a slow answer to an old query cannot overwrite a newer one. */
  #searchGeneration = 0;

  protected readonly connectionState = this.#connection.state;

  // ---- Pieces ----

  protected readonly column = viewChild.required<ElementRef<HTMLElement>>("column");
  protected readonly definitionBox = viewChild.required(DefinitionBox);
  protected readonly navigationMenu = viewChild.required(NavigationMenu);
  protected readonly settingsPanel = viewChild.required(SettingsPanel);
  protected readonly reviewOverlay = viewChild.required(ReviewOverlay);

  readonly #scroller = new SmoothScroller();
  #readingAnchor: ReadingAnchor | undefined;
  #progressReporter: ProgressReporter | undefined;

  // ---- Key modes ----

  readonly #definitionMode: KeyMode = {
    name: "definition",
    onEnter: () => this.#scroller.lock(),
    onExit: () => this.#scroller.unlock(),
    bindings: bindings({
      j: { description: "next sense", run: () => this.definitionBox().nextSense() },
      k: { description: "previous sense", run: () => this.definitionBox().previousSense() },
      s: { description: "save word", run: () => this.definitionBox().save() },
      d: { description: "delete word", run: () => this.definitionBox().deleteTerm() },
      t: { description: "translate", run: () => this.definitionBox().translate() },
      e: { description: "translation settings", run: () => this.definitionBox().editSettings() },
      Escape: { description: "close", run: () => this.#closeDefinitionBox() },
    }),
  };

  readonly #navigationMode: KeyMode = {
    name: "navigation",
    onEnter: () => this.#scroller.lock(),
    onExit: () => this.#scroller.unlock(),
    bindings: bindings({
      j: { description: "next option", run: () => this.navigationMenu().move(1) },
      k: { description: "previous option", run: () => this.navigationMenu().move(-1) },
      ArrowDown: { description: "next option", run: () => this.navigationMenu().move(1) },
      ArrowUp: { description: "previous option", run: () => this.navigationMenu().move(-1) },
      g: { description: "first option", run: () => this.navigationMenu().moveToFirst() },
      G: { description: "last option", run: () => this.navigationMenu().moveToLast() },
      Enter: { description: "select", run: () => this.navigationMenu().activate() },
      // `?` is what the previous menu used to reach the filter; `/` is the TUI habit.
      "/": { description: "filter", run: () => this.navigationMenu().focusFilter() },
      "?": { description: "filter", run: () => this.navigationMenu().focusFilter() },
      Escape: { description: "back", run: () => this.navigationMenu().back() },
    }),
  };

  readonly #settingsMode: KeyMode = {
    name: "settings",
    onEnter: () => this.#scroller.lock(),
    onExit: () => this.#scroller.unlock(),
    bindings: bindings({
      j: { description: "next", run: () => this.settingsPanel().move(1) },
      k: { description: "previous", run: () => this.settingsPanel().move(-1) },
      ArrowDown: { description: "next", run: () => this.settingsPanel().move(1) },
      ArrowUp: { description: "previous", run: () => this.settingsPanel().move(-1) },
      h: { description: "left / smaller", run: () => this.settingsPanel().across(-1) },
      l: { description: "right / larger", run: () => this.settingsPanel().across(1) },
      ArrowLeft: { description: "left / smaller", run: () => this.settingsPanel().across(-1) },
      ArrowRight: { description: "right / larger", run: () => this.settingsPanel().across(1) },
      Enter: { description: "edit", run: () => this.settingsPanel().activate() },
      c: { description: "close", run: () => this.settingsPanel().close() },
      Escape: { description: "close", run: () => this.settingsPanel().close() },
    }),
  };

  readonly #reviewMode: KeyMode = {
    name: "review",
    onEnter: () => this.#scroller.lock(),
    onExit: () => this.#scroller.unlock(),
    bindings: bindings({
      j: { description: "down", run: () => this.reviewOverlay().move(1) },
      k: { description: "up", run: () => this.reviewOverlay().move(-1) },
      ArrowDown: { description: "down", run: () => this.reviewOverlay().move(1) },
      ArrowUp: { description: "up", run: () => this.reviewOverlay().move(-1) },
      h: { description: "other side", run: () => this.reviewOverlay().across() },
      l: { description: "other side", run: () => this.reviewOverlay().across() },
      ArrowLeft: { description: "other side", run: () => this.reviewOverlay().across() },
      ArrowRight: { description: "other side", run: () => this.reviewOverlay().across() },
      Enter: { description: "pick / answer / next", run: () => this.reviewOverlay().activate() },
      v: { description: "speak", run: () => this.reviewOverlay().toggleVoice() },
      x: { description: "don't know", run: () => void this.reviewOverlay().giveUp() },
      Escape: { description: "close", run: () => this.reviewOverlay().close() },
    }),
  };

  readonly #readingMode: KeyMode = {
    name: "reading",
    bindings: bindings({
      j: { description: "scroll down", run: () => this.#scroller.start("down") },
      k: { description: "scroll up", run: () => this.#scroller.start("up") },
      d: { description: "define selection", run: () => this.#defineSelection() },
      t: { description: "translate selection", run: () => this.#translateSelection() },
      s: { description: "save selection", run: () => this.#saveSelection() },
      r: { description: "retry chapter", run: () => this.retryChapter() },
      n: { description: "navigation", run: () => this.openNavigation() },
      c: { description: "settings", run: () => this.settingsPanel().open() },
    }),
  };

  constructor() {
    this.#connection.definitions.pipe(takeUntilDestroyed()).subscribe((view) => {
      const box = this.definitionBox();
      // The box is already open on the word that was asked for; an answer for anything else is
      // one the reader has moved on from.
      if (!box.isOpen() || box.surfaceForm !== view.surfaceForm) return;

      box.showDefinition(view);
    });

    this.#connection.translations.pipe(takeUntilDestroyed()).subscribe(({ surfaceForm, translation }) => {
      const box = this.definitionBox();
      // A late answer for something the reader has already moved on from is dropped. Matched on
      // the surface form, because the definition may not have arrived to name a term yet (D32).
      if (!box.isOpen() || box.surfaceForm !== surfaceForm) return;

      // The server is the backstop for settings the page thought were there and were not.
      if (translation.error === "not-configured") {
        this.#openTranslationSettings();
        return;
      }

      box.showTranslation({
        text: translation.text,
        note: translation.note,
        error: translation.error === null ? null : describeTranslationFailure(translation.error),
      });
    });

    this.#connection.vocabularyChanges.pipe(takeUntilDestroyed()).subscribe(({ term, isSaved, forms }) => {
      // The box may be open on any form of the word — `running`, with `run` saved (D39).
      const box = this.definitionBox();
      const boxTerm = box.term;
      if (box.isOpen() && boxTerm !== undefined && (boxTerm === term || forms.includes(boxTerm))) {
        box.setSaved(isSaved);
      }

      const column = this.column().nativeElement;
      if (isSaved) underlineTerm(column, term, forms);
      else removeUnderline(column, term);
    });

    effect(() => {
      const state = this.connectionState();
      if (state !== undefined) this.#showConnectionState(state);
    });
  }

  ngAfterViewInit(): void {
    this.#document.body.classList.add("reading-page");
    this.#destroyRef.onDestroy(() => this.#document.body.classList.remove("reading-page"));
    markPointerMode();

    // The page decides where a reader lands — at their bookmark — so the browser's own memory
    // of the scroll offset, taken at some other width, must not be replayed on top of it.
    if ("scrollRestoration" in history) history.scrollRestoration = "manual";

    const column = this.column().nativeElement;

    /** Puts the reader back on their line when the column is re-laid out under them (D36). */
    this.#readingAnchor = new ReadingAnchor(column);
    this.#progressReporter = new ProgressReporter(column, (position) => {
      this.#connection.reportProgress(this.#novelName, position.chapterNumber, position.paragraphNumber);
      // The reader has settled somewhere; whatever is now far enough above them can go (D37).
      this.#trimChaptersAbove();
    });

    if (isTouchPrimary) this.#wireTouchSelectionAffordances(column);

    this.#router.push(this.#readingMode);
    this.#router.start();
    this.#destroyRef.onDestroy(() => this.#router.stop());

    const onScroll = (): void => this.#onScroll();
    window.addEventListener("scroll", onScroll);
    this.#destroyRef.onDestroy(() => window.removeEventListener("scroll", onScroll));

    void this.#start();
  }

  async #start(): Promise<void> {
    try {
      await this.#connection.start();
      const session = await this.#connection.getReadingSession();
      this.#novels = session.novels;
      this.#state.translationEmail.set(session.translationEmail);
      this.#state.translationLanguage.set(session.translationLanguage);
      this.#state.translationLanguages.set(session.translationLanguages);
      this.#state.assistant.set(session.assistant);

      await this.#openNovelAt(session.novelName, session.chapterNumber, session.paragraphNumber, session.resuming);
    } catch (error: unknown) {
      // `start` waits out an unreachable server rather than failing, so reaching here means the
      // first calls after it did not go through. The connection keeps trying underneath.
      console.error(error instanceof Error ? error.toString() : String(error));
      this.#showNotice("Could not reach the server.");
    }
  }

  // ---- Definition flow ----

  #defineSelection(): void {
    const selected = readSelection();
    if (!selected) return;

    this.#openBoxFor(selected.text, selected.rect, this.#paragraphOf(selected.range.startContainer));
  }

  /**
   * The text of the paragraph a node sits in, for the AI to read a selection against (D35).
   * A selection that runs across paragraphs is read against the one it starts in.
   */
  #paragraphOf(node: Node): string | null {
    const element = node instanceof Element ? node : node.parentElement;
    const paragraph = element?.closest("p");
    if (!paragraph || !this.column().nativeElement.contains(paragraph)) return null;

    return paragraph.textContent?.trim().replace(/\s+/g, " ") || null;
  }

  /** The paragraph for a translation, but only when the reader's AI is going to read it. */
  #contextToSend(): string | null {
    return this.#state.assistantEnabled() ? this.#boxParagraph : null;
  }

  /**
   * Opens the box on the word straight away and asks for its definition. The box shows
   * `loading…` until the answer lands — and says so if none does — so waiting never looks the
   * same as nothing happening (D27).
   */
  #openBoxFor(term: string, anchor: DOMRect, paragraph: string | null): void {
    this.#hideSelectionAffordances();
    this.#boxParagraph = paragraph;

    this.definitionBox().open(term, anchor, {
      onSave: (saved) => this.#connection.saveWord(this.#novelName, saved),
      onDelete: (saved) => this.#connection.deleteWord(saved),
      onTranslate: (saved) => this.#translateOrConfigure(saved),
      onEditSettings: () => this.#openTranslationSettings(),
      onClose: () => this.#closeDefinitionBox(),
    });

    clearSelection();
    if (!this.#router.has("definition")) this.#router.push(this.#definitionMode);

    this.#connection.requestDefinition(term, this.#boxParagraph);
  }

  /**
   * `t` on a selection of more than one word, and the phone's `t translate` button.
   *
   * The definition is asked for at the same moment and shown in the same box, but nothing waits
   * on it: a phrase is selected because the reader wants it translated, and the translation is
   * rendered the moment it lands, above a definition that is still loading (D32).
   */
  #translateSelected(selected: SelectedTerm): void {
    // One word already has `d`, and its translation lives inside that box.
    if (selected.wordCount < 2) return;

    this.#openBoxFor(selected.text, rectOfRange(selected.range) ?? selected.rect, this.#paragraphOf(selected.range.startContainer));
    this.#translateOrConfigure(selected.text);
  }

  #translateSelection(): void {
    const selected = readSelection();
    if (selected) this.#translateSelected(selected);
  }

  /**
   * `t`. A reader who has not set translation up is asked to, rather than being sent to a
   * server that can only answer "not configured" (D31) — the server still refuses that case,
   * but the reader should not need a round trip to be told what to do.
   */
  #translateOrConfigure(term: string): void {
    if (this.#state.translationEmail() === null || this.#state.translationLanguage() === null) {
      this.#openTranslationSettings();
      return;
    }

    this.definitionBox().showTranslationPending();
    this.#connection.requestTranslation(term, this.#contextToSend());
  }

  /** `e`, and the toolbar's settings button. Prefilled when there is something to edit. */
  #openTranslationSettings(): void {
    const box = this.definitionBox();
    if (!box.isOpen()) return;

    box.openSettings(
      {
        email: this.#state.translationEmail(),
        language: this.#state.translationLanguage(),
        languages: this.#state.translationLanguages(),
      },
      {
        onSubmit: (email, language) => void this.#applyTranslationSettings(email, language),
        onCancel: () => box.cancelSettings(),
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
  async #applyTranslationSettings(email: string, language: string): Promise<void> {
    const box = this.definitionBox();
    const asked = box.surfaceForm;

    box.cancelSettings();
    if (asked !== undefined) {
      box.showTranslationPending();
      this.#connection.requestTranslation(asked, this.#contextToSend(), { email, language });
    }

    const result = await this.#connection.saveTranslationSettings(email, language);

    if (result.error !== null) {
      // The server refused what the page accepted. Put the form back with the reason.
      this.#openTranslationSettings();
      box.showSettingsError(
        result.error === "email-invalid"
          ? "The server would not accept that email address."
          : "The server would not accept that language.",
        result.error === "email-invalid" ? "email" : "language",
      );
      return;
    }

    this.#state.translationEmail.set(result.email);
    this.#state.translationLanguage.set(result.language);
  }

  #saveSelection(): void {
    const selected = readSelection();
    if (!selected) return;

    const text = selected.text;
    this.savingSelection.set(text);
    void this.#connection.saveWord(this.#novelName, text).finally(() => {
      if (this.savingSelection() === text) this.savingSelection.set(undefined);
    });
  }

  #closeDefinitionBox(): void {
    this.definitionBox().close();
    this.#router.pop("definition");
    this.#boxParagraph = null;
  }

  /** Already-underlined words are clickable. */
  protected onColumnClick(event: MouseEvent): void {
    const target = event.target;
    if (!(target instanceof HTMLElement)) return;

    const knownWord = target.closest<HTMLElement>(".known-word");
    const term = knownWord?.dataset["term"];
    if (!knownWord || !term) return;

    this.#openBoxFor(term, rectOf(knownWord), this.#paragraphOf(knownWord));
  }

  // ---- Rendering ----

  /** Paragraph 1 of a chapter is its title. */
  protected isChapterTitle(block: Block): boolean {
    return block.kind === "paragraph" && block.number === 1;
  }

  /**
   * Sets the column's contents and draws them now, not at the next change detection: every
   * caller goes on to measure or scroll against what was just drawn.
   */
  #renderBlocks(update: (blocks: readonly Block[]) => readonly Block[]): void {
    this.blocks.update(update);
    this.#changeDetector.detectChanges();
  }

  #appendChapter(chapter: ChapterView): void {
    const paragraphs: Block[] = chapter.paragraphs.map((paragraph) => ({
      kind: "paragraph",
      key: `${chapter.chapterNumber}:${paragraph.number}`,
      chapter: chapter.chapterNumber,
      number: paragraph.number,
      markup: paragraph.markup,
    }));

    this.#renderBlocks((blocks) => [...blocks, ...paragraphs]);
  }

  #showNotice(text: string): void {
    this.#renderBlocks((blocks) => [...blocks, { kind: "notice", key: `notice:${++this.#noticeCount}`, text }]);
  }

  // ---- Keeping the page short ----

  /** The chapter numbers on the page, top to bottom. */
  #chaptersOnPage(): number[] {
    const numbers: number[] = [];
    for (const block of this.blocks()) {
      if (block.kind === "paragraph" && numbers.at(-1) !== block.chapter) numbers.push(block.chapter);
    }
    return numbers;
  }

  /**
   * Removes the chapters the reader has left well behind: everything more than
   * {@link chaptersKeptAbove} chapters above the one they are reading, and — whatever happens —
   * enough of the oldest to keep the page at {@link maximumChaptersOnPage}.
   *
   * Only ever chapters above the one being read, so everything removed is off the top of the
   * screen. Taking it out would pull the text the reader is looking at upwards by its height;
   * the paragraph on screen is measured before and after and the scroll offset is corrected by
   * the difference, so nothing visible moves. (A browser's own scroll anchoring may already have
   * corrected it, in which case the difference is nothing.)
   */
  #trimChaptersAbove(): void {
    const position = this.#progressReporter?.currentPosition();
    if (!position) return;

    const chapters = this.#chaptersOnPage();
    const excess = chapters.length - maximumChaptersOnPage;
    const doomed = new Set(chapters.filter((chapter, index) =>
      chapter < position.chapterNumber
      && (chapter < position.chapterNumber - chaptersKeptAbove || index < excess)));
    if (doomed.size === 0) return;

    const anchor = this.#paragraphElement(position.chapterNumber, position.paragraphNumber);
    const topBefore = anchor?.getBoundingClientRect().top;

    this.#renderBlocks((blocks) => blocks.filter((block) => block.kind !== "paragraph" || !doomed.has(block.chapter)));

    if (anchor && topBefore !== undefined) {
      const shift = anchor.getBoundingClientRect().top - topBefore;
      if (shift !== 0) window.scrollBy(0, shift);
    }

    this.#readingAnchor?.record();
  }

  #paragraphElement(chapterNumber: number, paragraphNumber: number): HTMLElement | null {
    return this.column().nativeElement.querySelector<HTMLElement>(
      `[data-chapter="${chapterNumber}"][data-paragraph="${paragraphNumber}"]`,
    );
  }

  async #loadAndAppend(chapterNumber: number): Promise<ChapterView> {
    this.chapterUnavailable.set(undefined);
    this.#retryUnavailable = undefined;
    this.chapterInFlight.set(chapterNumber);
    let chapter: ChapterView;
    try {
      chapter = await this.#connection.loadChapter(this.#novelName, chapterNumber);
    } finally {
      // Cleared before the chapter is rendered, so the two are never both in the column while
      // it is measured (D36, D37).
      this.chapterInFlight.set(undefined);
    }

    if (chapter.found) {
      this.#appendChapter(chapter);
      this.#lastLoadedChapter = Math.max(this.#lastLoadedChapter, chapterNumber);
    } else if (chapter.failure === "unavailable") {
      // The site did not serve it, which is not the end of the novel: the reader is offered a
      // retry where the chapter would have been (D49). The caller says what retrying means.
      this.chapterUnavailable.set(chapterNumber);
    } else if (!chapter.failed) {
      // An empty chapter is the end of the novel. A call that never got through is not: the
      // page waits for the connection instead of deciding the novel has run out (D28).
      this.#reachedEnd = true;
    }

    return chapter;
  }

  /** The `retry` button and `r`: the chapter that did not load, asked for again (D49). */
  protected retryChapter(): void {
    const retry = this.#retryUnavailable;
    if (!retry || this.#loadingChapter) return;
    this.#retryUnavailable = undefined;
    retry();
  }

  /**
   * Waits for the reading column to stop moving.
   *
   * The column is set in a web font. Until it loads, every paragraph is laid out at the
   * fallback font's metrics and then shifts when it swaps in — so a bookmark restored before
   * that happens is measured against a layout that no longer exists, and the paragraph ends up
   * off screen. Costs nothing once the font is cached.
   */
  async #waitForStableLayout(): Promise<void> {
    try {
      await this.#document.fonts?.ready;
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
  #scrollParagraphToBottom(chapterNumber: number, paragraphNumber: number): boolean {
    const element = this.#paragraphElement(chapterNumber, paragraphNumber);
    if (!element) return false;

    const rect = element.getBoundingClientRect();
    window.scrollTo({ top: window.scrollY + rect.bottom - window.innerHeight });
    return true;
  }

  /**
   * Opens a novel at a position, loading one chapter — or two, when the bookmark sits near the
   * top of its chapter and there would otherwise be nothing above it.
   */
  async #openNovelAt(novel: string, chapterNumber: number, paragraphNumber: number, resuming: boolean): Promise<void> {
    if (this.#loadingChapter) return;

    this.#loadingChapter = true;
    try {
      this.#renderBlocks(() => []);
      this.#novelName = novel;
      this.#lastLoadedChapter = 0;
      this.#reachedEnd = false;
      this.noChapterYet.set(undefined);

      if (resuming && paragraphNumber <= nearTopOfChapter && chapterNumber > 1) {
        await this.#loadAndAppend(chapterNumber - 1);
        // A missing predecessor is not a reason to stop; the wanted chapter still loads.
        this.#reachedEnd = false;
      }

      const chapter = await this.#loadAndAppend(chapterNumber);
      if (!chapter.found) {
        if (chapter.failed) {
          this.#retryWhenConnected = () => void this.#openNovelAt(novel, chapterNumber, paragraphNumber, resuming);
          return;
        }

        if (chapter.failure === "unavailable") {
          // The line under the empty column says so and offers the retry (D49).
          this.#retryUnavailable = () => void this.#openNovelAt(novel, chapterNumber, paragraphNumber, resuming);
          return;
        }

        this.#showNotice(`Chapter ${chapterNumber} of “${novel}” could not be loaded.`);
        return;
      }

      if (resuming) {
        // Must not be scrolled before the column has settled, or the bookmark lands elsewhere.
        await this.#waitForStableLayout();

        if (this.#scrollParagraphToBottom(chapterNumber, paragraphNumber)) {
          this.#progressReporter?.markReported({ chapterNumber, paragraphNumber });
          this.#readingAnchor?.record();
          return;
        }
      }

      window.scrollTo({ top: 0 });
      this.#readingAnchor?.record();
    } finally {
      this.#loadingChapter = false;
    }
  }

  #onScroll(): void {
    // Report where the reader is, once they stop.
    this.#progressReporter?.scheduleReport();

    if (this.#scroller.isLocked || this.#loadingChapter || this.#reachedEnd) return;

    const reachedBottom = window.innerHeight + window.scrollY >= this.#document.body.scrollHeight - 100;
    if (!reachedBottom) return;

    // Scrolling on at the bottom asks again for a chapter that did not load — and only for that:
    // after a failed jump the column is empty, and "the next chapter" would be chapter 1 (D49).
    if (this.#retryUnavailable) {
      this.retryChapter();
      return;
    }

    this.#loadNextChapter(this.#lastLoadedChapter + 1);
  }

  /**
   * Rolls the novel forward by one chapter. A load the connection cut short is asked for again
   * as soon as the connection is back — the reader is at the bottom of the page, so there is no
   * further scroll event coming to try again with (D28).
   */
  #loadNextChapter(chapterNumber: number): void {
    if (this.#loadingChapter || this.#reachedEnd) return;

    this.#loadingChapter = true;
    void this.#loadAndAppend(chapterNumber)
      .then((chapter) => {
        if (chapter.failed) this.#retryWhenConnected = () => this.#loadNextChapter(chapterNumber);
        if (chapter.failure === "unavailable") this.#retryUnavailable = () => this.#loadNextChapter(chapterNumber);
        // Rolling forward off the last published chapter: said once, quietly (D49).
        else if (!chapter.found && !chapter.failed) this.noChapterYet.set(chapterNumber);
        // A new chapter below is the moment the page could exceed its limit (D37).
        if (chapter.found) this.#trimChaptersAbove();
      })
      .finally(() => {
        this.#loadingChapter = false;
      });
  }

  // ---- Touch input ----

  #hideSelectionAffordances(): void {
    this.#pendingSelection = undefined;
    this.defineAffordanceShown.set(false);
    this.translateAffordanceShown.set(false);
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
  #wireTouchSelectionAffordances(column: HTMLElement): void {
    let settleTimer: number | undefined;

    const onSelectionChange = (): void => {
      if (settleTimer !== undefined) clearTimeout(settleTimer);

      settleTimer = window.setTimeout(() => {
        settleTimer = undefined;

        const anchor = window.getSelection()?.anchorNode;
        const inside = anchor !== null && anchor !== undefined && column.contains(anchor);
        const selected = this.definitionBox().isOpen() || !inside ? undefined : readSelection();
        this.#pendingSelection = selected;
        this.defineAffordanceShown.set(selected !== undefined);
        // A single word is already covered by the definition and the `t` inside its box; the
        // button is for the phrase case, where there is no definition to hang it off.
        this.translateAffordanceShown.set(selected !== undefined && selected.wordCount >= 2);
      }, touchSelectionSettleMs);
    };

    this.#document.addEventListener("selectionchange", onSelectionChange);
    this.#destroyRef.onDestroy(() => this.#document.removeEventListener("selectionchange", onSelectionChange));
  }

  /**
   * Measured at the tap rather than when the button appeared: the page may have been scrolled
   * in between, and the box has to point at where the words are.
   */
  protected defineTapped(): void {
    const selected = this.#pendingSelection;
    if (!selected) return;

    this.#openBoxFor(selected.text, rectOfRange(selected.range) ?? selected.rect, this.#paragraphOf(selected.range.startContainer));
  }

  protected translateTapped(): void {
    const selected = this.#pendingSelection;
    if (selected) this.#translateSelected(selected);
  }

  // ---- Navigation menu ----

  /**
   * The hint is also the control: tapping it opens the menu that `n` opens. On a keyboard it
   * stays a decorative hint (pointer-events are off in CSS, so this never fires).
   */
  protected openNavigation(): void {
    const menu = this.navigationMenu();
    if (menu.isOpen()) return;

    // The menu covers the corner the affordances sit in.
    this.#hideSelectionAffordances();

    // The mode goes on first so that a menu closed during `open` still pops cleanly.
    if (!this.#router.has("navigation")) this.#router.push(this.#navigationMode);
    menu.open(this.#rootScreen(), () => this.#router.pop("navigation"));
  }

  #rootScreen(): MenuScreen {
    const menu = this.navigationMenu();
    return {
      title: "navigation",
      filterPlaceholder: "filter options…",
      items: [
        {
          label: "Go to chapter",
          detail: `reading ${this.#lastLoadedChapter}`,
          run: () => menu.push(this.#chapterScreen()),
        },
        {
          label: "Novels you've read",
          detail: this.#novels.length > 0 ? `${this.#novels.length}` : "none yet",
          run: () => menu.push(this.#libraryScreen()),
        },
        {
          label: "Search new novels",
          detail: "novelphoenix",
          run: () => menu.push(this.#searchScreen()),
        },
        {
          label: "Sign out",
          run: () => void this.#signOut(),
        },
      ],
    };
  }

  /**
   * Sign-out is a POST (D42) — as a link, any page on the web could sign the reader out. The
   * login screen comes next either way: if the request failed, the cookie may still be there,
   * and the login screen sends a signed-in reader straight back.
   */
  async #signOut(): Promise<void> {
    try {
      await fetch(appUrl("auth/signout"), { method: "POST", credentials: "same-origin" });
    } finally {
      window.location.href = appUrl("Login");
    }
  }

  #chapterScreen(): MenuScreen {
    const menu = this.navigationMenu();
    const current = this.#lastLoadedChapter;
    const first = Math.max(1, current - chapterWindow);
    const last = current + chapterWindow;
    const goTo = (target: number): void => {
      menu.close();
      void this.#openNovelAt(this.#novelName, target, 1, false);
    };

    const items: MenuItem[] = [];
    for (let target = first; target <= last; target += 1) {
      const chapter = target;
      items.push({
        label: `Chapter ${chapter}`,
        detail: chapter === current ? "current" : undefined,
        run: () => goTo(chapter),
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

        return [{ label: `Chapter ${wanted}`, detail: "not in the list — try it", run: () => goTo(wanted) }];
      },
    };
  }

  /**
   * The novels the server said this reader has open, shown the same way search hits are. A
   * novel the catalogue has not been asked about yet falls back to its slug and carries no
   * figures — it gets both once the daily refresh has run (D22).
   */
  #libraryScreen(): MenuScreen {
    const menu = this.navigationMenu();
    return {
      title: "library",
      items: this.#novels.map((novel) => ({
        label: novel.title ?? novel.slug,
        detail: novel.slug === this.#novelName ? "reading" : describeNovel(novel),
        run: () => {
          menu.close();
          void this.#resumeNovel(novel.slug);
        },
      })),
      emptyMessage: "No novels yet — the ones you read will show up here.",
      filterPlaceholder: "filter novels…",
    };
  }

  /**
   * Novel search. The filter field is the query: the menu sends it once the reader has stopped
   * typing, and the rows are whatever came back.
   */
  #searchScreen(): MenuScreen {
    return {
      title: "search",
      items: [],
      emptyMessage: "Enter the name of the novel or part of it.",
      filterPlaceholder: "search novels…",
      onQuery: (query) => void this.#runSearch(query),
    };
  }

  async #runSearch(query: string): Promise<void> {
    const menu = this.navigationMenu();
    const trimmed = query.trim();
    const generation = ++this.#searchGeneration;

    if (trimmed.length < minimumSearchLength) {
      menu.setItems([], "Enter the name of the novel or part of it.");
      return;
    }

    menu.setItems([], `Searching for “${trimmed}”…`);

    const results = await this.#connection.searchNovels(trimmed);

    // A slower earlier search must not land on top of a later one.
    if (generation !== this.#searchGeneration) return;

    menu.setItems(
      results.map((novel) => ({
        label: novel.title,
        detail: describeNovel(novel),
        run: () => {
          menu.close();
          void this.#resumeNovel(novel.slug);
        },
      })),
      `Nothing found for “${trimmed}”.`,
    );
  }

  /** Opens another novel where that novel was left, not where the current one was. */
  async #resumeNovel(novel: string): Promise<void> {
    const progress = await this.#connection.getNovelProgress(novel);
    await this.#openNovelAt(novel, progress.chapterNumber, progress.paragraphNumber, progress.resuming);
  }

  // ---- Settings drawer ----

  /**
   * The paragraph that was at the bottom of the screen when a run of font-size changes began —
   * a slider drag, or h/l held down. Kept for the whole run, so each step restores the same
   * paragraph instead of whichever one the previous step's rounding left there.
   */
  #fontChangeParagraph: ReadingPosition | undefined;
  #fontChangeSettleTimer: number | undefined;

  /**
   * Every font-size change goes through here (D36). The paragraph the reader has at the bottom
   * of the screen — the one their bookmark records — is put back at the bottom straight after
   * the change, and checked again once the layout has settled.
   */
  protected readonly keepReadingPosition = (change: () => void): void => {
    this.#fontChangeParagraph ??= this.#progressReporter?.currentPosition();
    change();

    const position = this.#fontChangeParagraph;
    if (!position) return;

    this.#scrollParagraphToBottom(position.chapterNumber, position.paragraphNumber);

    if (this.#fontChangeSettleTimer !== undefined) clearTimeout(this.#fontChangeSettleTimer);
    this.#fontChangeSettleTimer = window.setTimeout(() => {
      this.#fontChangeSettleTimer = undefined;
      this.#fontChangeParagraph = undefined;
      this.#scrollParagraphToBottom(position.chapterNumber, position.paragraphNumber);
      this.#readingAnchor?.record();
    }, fontChangeSettleInMilliseconds);
  };

  /** The drawer covers the page; nothing else should be left open behind it. */
  protected onSettingsOpened(): void {
    if (this.definitionBox().isOpen()) this.#closeDefinitionBox();
    if (this.navigationMenu().isOpen()) this.navigationMenu().close();
    this.#hideSelectionAffordances();
    clearSelection();
    if (!this.#router.has("settings")) this.#router.push(this.#settingsMode);
  }

  protected onSettingsClosed(): void {
    this.#router.pop("settings");
  }

  // ---- Review ----

  /** The drawer's `start review`: the drawer makes way, and the review opens over the page. */
  protected startReview(): void {
    this.settingsPanel().close();
    this.reviewOverlay().open();
  }

  /** Like the drawer, the review covers the page; nothing is left open behind it. */
  protected onReviewOpened(): void {
    if (this.definitionBox().isOpen()) this.#closeDefinitionBox();
    if (this.navigationMenu().isOpen()) this.navigationMenu().close();
    this.#hideSelectionAffordances();
    clearSelection();
    if (!this.#router.has("review")) this.#router.push(this.#reviewMode);
  }

  protected onReviewClosed(): void {
    this.#router.pop("review");
  }

  // ---- Connection state ----

  /**
   * The bottom-left chip is the one visible sign that the link to the server is down. It
   * appears while the client is reconnecting — which it does once a second, for as long as it
   * takes — and takes anything that was cut short with it when the connection returns (D28).
   */
  #showConnectionState(state: ConnectionState): void {
    // The box says "still looking…" or "connection lost" depending on this (D33).
    this.definitionBox().setConnectionUp(state === "connected");

    if (state !== "connected") return;

    const retry = this.#retryWhenConnected;
    this.#retryWhenConnected = undefined;
    retry?.();
  }
}

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
