import {
  ChangeDetectionStrategy,
  Component,
  type ElementRef,
  afterRenderEffect,
  computed,
  signal,
  viewChild,
} from "@angular/core";
import type { MenuItem, MenuScreen } from "./menu-screen";

/**
 * The navigation menu: a TUI panel of options driven entirely from the keyboard.
 *
 * It is a stack of screens rather than a single list. Activating an option can push
 * another screen (chapters, library, search); Escape pops back and closes at the root, so
 * the breadcrumb in the header always says where you are.
 *
 * Keys are *not* handled here. The menu exposes `move`/`activate`/`back` and the page binds
 * them through the KeyboardRouter, so the menu is a mode like any other (D6). The one
 * exception is the filter field, which owns its own keystrokes while it has focus — the
 * router deliberately keeps its hands off text entry.
 */

/**
 * How long the reader must stop typing before a query screen asks the server. Long enough
 * that a typed word is one request rather than one per letter.
 */
const queryDelayInMilliseconds = 1500;

/** Case-insensitive substring match, and where it hit, so the row can pick it out. */
interface MatchedItem {
  readonly item: MenuItem;
  readonly before: string;
  readonly hit: string;
  readonly after: string;
}

@Component({
  selector: "app-navigation-menu",
  templateUrl: "./navigation-menu.html",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NavigationMenu {
  /** Screen stack; the last entry is the one on show. */
  protected readonly screens = signal<readonly MenuScreen[]>([]);
  protected readonly index = signal(0);
  protected readonly filterText = signal("");

  readonly isOpen = computed(() => this.screens().length > 0);
  protected readonly screen = computed(() => this.screens().at(-1));

  /** Every crumb but the last is dim: "navigation / chapters". */
  protected readonly trail = computed(() => this.screens().slice(0, -1).map((screen) => `${screen.title} / `));

  protected readonly matches = computed<readonly MatchedItem[]>(() => {
    const screen = this.screen();
    if (!screen) return [];

    const filterText = this.filterText();
    // A query screen's rows already are the answer; filtering them again would hide results.
    const matched = screen.onQuery !== undefined
      ? screen.items.map(unmatched)
      : matchItems(screen.items, filterText);

    // Derived rows come from the filter text rather than from the list, so nothing in them is
    // a "match" to highlight.
    const derived = (screen.itemsFromFilter?.(filterText) ?? []).map(unmatched);
    return derived.length > 0 ? [...matched, ...derived] : matched;
  });

  /** The cursor, clamped to the rows there are. */
  protected readonly selected = computed(() => Math.min(this.index(), Math.max(0, this.matches().length - 1)));

  protected readonly emptyMessage = computed(() => {
    const screen = this.screen();
    return this.filterText().length > 0 && screen?.onQuery === undefined
      ? `No option matches “${this.filterText()}”.`
      : (screen?.emptyMessage ?? "Nothing here yet.");
  });

  protected readonly counter = computed(() =>
    this.matches().length === 0 ? "0" : `${this.selected() + 1}/${this.matches().length}`);

  /** Whether the selected row's name is wider than its row, so h/l have something to scroll. */
  protected readonly selectedOverflows = signal(false);

  protected readonly hints = computed(() =>
    [
      "j/k move",
      this.selectedOverflows() ? "h/l name" : undefined,
      "enter select",
      "/ filter",
      this.screens().length > 1 ? "esc back" : "esc close",
    ].filter((hint) => hint !== undefined).join("  ·  "));

  protected readonly filter = viewChild<ElementRef<HTMLInputElement>>("filter");
  protected readonly list = viewChild<ElementRef<HTMLElement>>("list");

  #queryTimer: number | undefined;
  /** Called when the last screen is popped, so the caller can drop the keyboard mode. */
  #onClosed: (() => void) | undefined;

  constructor() {
    afterRenderEffect(() => {
      this.matches();
      const list = this.list()?.nativeElement;
      if (list) applyOverflowFades(list);
    });

    afterRenderEffect(() => {
      const selected = this.selected();
      this.list()?.nativeElement.children[selected]?.scrollIntoView({ block: "nearest" });
      const label = this.#selectedLabel();
      this.selectedOverflows.set(label !== undefined && overflowOf(label) > 1);
    });
  }

  /** True while the filter field has focus, i.e. while the router is standing aside. */
  get isFiltering(): boolean {
    const filter = this.filter()?.nativeElement;
    return filter !== undefined && document.activeElement === filter;
  }

  open(screen: MenuScreen, onClosed?: () => void): void {
    if (this.isOpen()) this.close();

    this.#onClosed = onClosed;
    this.filterText.set("");
    this.screens.set([screen]);
    this.index.set(startingIndexOf(screen));
  }

  /** Descends into a sub-screen, keeping the breadcrumb. */
  push(screen: MenuScreen): void {
    if (!this.isOpen()) return;

    this.#cancelQuery();
    this.screens.update((screens) => [...screens, screen]);
    this.filterText.set("");
    this.index.set(startingIndexOf(screen));
  }

  /** Escape: up one screen, or shut the menu when already at the root. */
  back(): void {
    if (!this.isOpen()) return;

    this.#cancelQuery();

    if (this.filterText().length > 0) {
      this.filterText.set("");
      this.index.set(0);
      return;
    }

    if (this.screens().length <= 1) {
      this.close();
      return;
    }

    this.screens.update((screens) => screens.slice(0, -1));
    this.index.set(startingIndexOf(this.screen()));
  }

  close(): void {
    this.#cancelQuery();
    this.screens.set([]);
    this.index.set(0);
    this.filterText.set("");

    const onClosed = this.#onClosed;
    this.#onClosed = undefined;
    onClosed?.();
  }

  /**
   * h and l: slides the selected row's name along, most of a row's width at a time — a book's
   * full title often does not fit, and its volume is at the end. On touch the name is swiped.
   */
  scrollName(step: 1 | -1): void {
    const label = this.#selectedLabel();
    if (label) label.scrollBy({ left: step * Math.max(1, label.clientWidth * 0.6) });
  }

  /** Keeps a scrolled name's fades in step with which of its ends are out of sight. */
  protected onLabelScroll(event: Event): void {
    if (event.target instanceof HTMLElement) applyOverflowFade(event.target);
  }

  #selectedLabel(): HTMLElement | undefined {
    return this.list()?.nativeElement.children[this.selected()]?.querySelector<HTMLElement>(".navigation-list__label") ?? undefined;
  }

  /** Wraps at both ends. */
  move(step: number): void {
    const count = this.matches().length;
    if (count === 0) return;

    const next = this.selected() + step;
    this.index.set(next > count - 1 ? 0 : next < 0 ? count - 1 : next);
  }

  moveToFirst(): void {
    this.index.set(0);
  }

  moveToLast(): void {
    this.index.set(Math.max(0, this.matches().length - 1));
  }

  /**
   * Replaces the current screen's rows — the answer to a query, or the message shown while
   * one is in flight. Deliberately leaves the filter field and its focus alone, so results
   * landing mid-sentence do not interrupt typing.
   */
  setItems(items: readonly MenuItem[], emptyMessage?: string): void {
    const screen = this.screen();
    if (!screen) return;

    this.screens.update((screens) => [
      ...screens.slice(0, -1),
      { ...screen, items, emptyMessage: emptyMessage ?? screen.emptyMessage },
    ]);
    this.index.set(0);
  }

  activate(): void {
    const matched = this.matches()[this.selected()];
    // `run` may push a screen or close the menu, so it goes last.
    matched?.item.run?.();
  }

  focusFilter(): void {
    this.filter()?.nativeElement.focus();
  }

  /** A tapped (or clicked) row is a select-then-activate — the pointer's Enter. */
  protected activateRow(position: number): void {
    this.index.set(position);
    this.activate();
  }

  protected onFilterInput(value: string): void {
    this.filterText.set(value);
    this.index.set(0);

    const screen = this.screen();
    // The server decides what a query screen holds; the rows stand until it answers.
    if (screen?.onQuery) this.#scheduleQuery(screen.onQuery, value);
  }

  /**
   * The filter field owns its keys while focused. Enter activates the selection without
   * leaving the field, and Escape hands control back to the list rather than closing the
   * menu outright — losing a whole screen to a stray Escape is the annoying version.
   */
  protected onFilterKey(event: KeyboardEvent, filter: HTMLInputElement): void {
    if (event.key === "Escape") {
      event.preventDefault();
      filter.blur();
    } else if (event.key === "Enter") {
      event.preventDefault();
      this.activate();
    } else if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      this.move(event.key === "ArrowDown" ? 1 : -1);
    }
  }

  /** Restarts the clock on every keystroke, so only the settled query is sent. */
  #scheduleQuery(run: (query: string) => void, query: string): void {
    this.#cancelQuery();

    this.#queryTimer = window.setTimeout(() => {
      this.#queryTimer = undefined;
      run(query);
    }, queryDelayInMilliseconds);
  }

  #cancelQuery(): void {
    if (this.#queryTimer === undefined) return;

    clearTimeout(this.#queryTimer);
    this.#queryTimer = undefined;
  }
}

/** Clamped so a screen cannot ask for a row it does not have. */
function startingIndexOf(screen: MenuScreen | undefined): number {
  if (!screen) return 0;

  const requested = screen.initialIndex ?? 0;
  return Math.max(0, Math.min(requested, screen.items.length - 1));
}

function unmatched(item: MenuItem): MatchedItem {
  return { item, before: item.label, hit: "", after: "" };
}

/** Rows whose label contains the filter, in screen order. An empty filter matches all. */
function matchItems(items: readonly MenuItem[], filterText: string): MatchedItem[] {
  const needle = filterText.trim().toLowerCase();
  if (needle.length === 0) return items.map(unmatched);

  const matched: MatchedItem[] = [];
  for (const item of items) {
    const at = item.label.toLowerCase().indexOf(needle);
    if (at < 0) continue;

    matched.push({
      item,
      before: item.label.slice(0, at),
      hit: item.label.slice(at, at + needle.length),
      after: item.label.slice(at + needle.length),
    });
  }

  return matched;
}

/**
 * A row is one line. A label too long for its share of it scrolls sideways — swiped on a phone,
 * h/l on a keyboard — so a book's full title, volume and all, can be read, while the dim column
 * on the right (a novel's rank and length) keeps its place.
 *
 * Whichever end has more of the name beyond it is faded, so a clipped name visibly runs on
 * rather than looking truncated: the right end at first, the left once it has been scrolled,
 * both in between. Measured rather than guessed: only the browser knows how wide the text is.
 */
function applyOverflowFades(list: HTMLElement): void {
  for (const label of Array.from(list.querySelectorAll<HTMLElement>(".navigation-list__label"))) {
    applyOverflowFade(label);
  }
}

/** How far the label's text runs past its box, in pixels. */
function overflowOf(label: HTMLElement): number {
  return label.scrollWidth - label.clientWidth;
}

function applyOverflowFade(label: HTMLElement): void {
  // A pixel of slack: sub-pixel text widths make an exactly-fitting label look wider.
  const overflow = overflowOf(label);
  label.classList.toggle("navigation-list__label--more-before", overflow > 1 && label.scrollLeft > 1);
  label.classList.toggle("navigation-list__label--more-after", overflow > 1 && overflow - label.scrollLeft > 1);
}
