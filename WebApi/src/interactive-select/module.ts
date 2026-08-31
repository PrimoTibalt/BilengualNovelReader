/**
 * The navigation menu: a TUI panel of options driven entirely from the keyboard.
 *
 * It is a stack of screens rather than a single list. Activating an option can push
 * another screen (chapters, library, search); Escape pops back and closes at the root, so
 * the breadcrumb in the header always says where you are.
 *
 * Keys are *not* handled here. The menu exposes `move`/`activate`/`back` and the caller
 * binds them through the KeyboardRouter, so the menu is a mode like any other (D6). The
 * one exception is the filter field, which owns its own keystrokes while it has focus —
 * the router deliberately keeps its hands off text entry.
 *
 * Built with plain DOM calls and no library (D5). Labels are inserted as text nodes.
 */

/** One row. `run` is what Enter does; a row without one is inert (a heading or a stub). */
export interface MenuItem {
  readonly label: string;
  /** Dim right-aligned text: a count, "current", a hint about why a row does nothing. */
  readonly detail?: string;
  readonly run?: () => void;
}

export interface MenuScreen {
  /** Last crumb in the header breadcrumb. */
  readonly title: string;
  readonly items: readonly MenuItem[];
  /** Shown when the screen has no items at all, or when the filter matches none. */
  readonly emptyMessage?: string;
  readonly filterPlaceholder?: string;
  /** Row selected on arrival — the chapter list opens on the chapter being read. */
  readonly initialIndex?: number;
  /**
   * Makes the filter field a *server* query rather than a local filter: the rows are
   * whatever the server last answered, so filtering them again here would hide results the
   * server deliberately returned. Called once the reader stops typing.
   */
  readonly onQuery?: (query: string) => void;
  /**
   * Rows built from the filter text itself, shown after the matches. This is how a screen
   * offers something its list does not contain — a chapter outside the range it shows —
   * rather than answering "no match" to a perfectly good request.
   *
   * Called on every keystroke, so keep it cheap and return `[]` when the text offers nothing.
   */
  readonly itemsFromFilter?: (filterText: string) => readonly MenuItem[];
}

/**
 * How long the reader must stop typing before a query screen asks the server. Long enough
 * that a typed word is one request rather than one per letter.
 */
const queryDelayInMilliseconds = 1500;

/** Case-insensitive substring match, and where it hit, so the row can pick it out. */
interface MatchedItem {
  readonly item: MenuItem;
  readonly matchStart: number;
  readonly matchLength: number;
}

export class NavigationMenu {
  readonly #host: HTMLElement;
  readonly #affordance: HTMLElement | undefined;

  /** Screen stack; the last entry is the one on show. */
  readonly #screens: MenuScreen[] = [];

  #panel: HTMLElement | undefined;
  #title: HTMLElement | undefined;
  #counter: HTMLElement | undefined;
  #body: HTMLElement | undefined;
  #filter: HTMLInputElement | undefined;
  #hints: HTMLElement | undefined;

  #matches: MatchedItem[] = [];
  #index = 0;
  #filterText = "";
  #queryTimer: number | undefined;

  /** Called when the last screen is popped, so the caller can drop the keyboard mode. */
  #onClosed: (() => void) | undefined;

  constructor(host: HTMLElement, affordance?: HTMLElement) {
    this.#host = host;
    this.#affordance = affordance;
  }

  get isOpen(): boolean {
    return this.#panel !== undefined;
  }

  /** True while the filter field has focus, i.e. while the router is standing aside. */
  get isFiltering(): boolean {
    return this.#filter !== undefined && document.activeElement === this.#filter;
  }

  open(screen: MenuScreen, onClosed?: () => void): void {
    if (this.isOpen) this.close();

    this.#onClosed = onClosed;
    this.#screens.length = 0;
    this.#screens.push(screen);
    this.#index = startingIndexOf(screen);
    this.#build();
    this.#render();
  }

  /** Descends into a sub-screen, keeping the breadcrumb. */
  push(screen: MenuScreen): void {
    if (!this.isOpen) return;

    this.#cancelQuery();
    this.#screens.push(screen);
    this.#filterText = "";
    this.#index = startingIndexOf(screen);
    this.#render();
  }

  /** Escape: up one screen, or shut the menu when already at the root. */
  back(): void {
    if (!this.isOpen) return;

    this.#cancelQuery();

    if (this.#filterText.length > 0) {
      this.#filterText = "";
      this.#index = 0;
      this.#render();
      return;
    }

    if (this.#screens.length <= 1) {
      this.close();
      return;
    }

    this.#screens.pop();
    this.#index = startingIndexOf(this.#currentScreen());
    this.#render();
  }

  close(): void {
    this.#cancelQuery();
    this.#panel?.remove();
    this.#panel = undefined;
    this.#title = undefined;
    this.#counter = undefined;
    this.#body = undefined;
    this.#filter = undefined;
    this.#hints = undefined;
    this.#screens.length = 0;
    this.#matches = [];
    this.#index = 0;
    this.#filterText = "";

    this.#host.hidden = true;
    if (this.#affordance) this.#affordance.hidden = false;

    const onClosed = this.#onClosed;
    this.#onClosed = undefined;
    onClosed?.();
  }

  /** Wraps at both ends, the way the old list did. */
  move(step: number): void {
    if (this.#matches.length === 0) return;

    const lastIndex = this.#matches.length - 1;
    const next = this.#index + step;
    this.#index = next > lastIndex ? 0 : next < 0 ? lastIndex : next;
    this.#renderSelection();
  }

  moveToFirst(): void {
    if (this.#matches.length === 0) return;
    this.#index = 0;
    this.#renderSelection();
  }

  moveToLast(): void {
    if (this.#matches.length === 0) return;
    this.#index = this.#matches.length - 1;
    this.#renderSelection();
  }

  /**
   * Replaces the current screen's rows — the answer to a query, or the message shown while
   * one is in flight. Deliberately leaves the filter field and its focus alone, so results
   * landing mid-sentence do not interrupt typing.
   */
  setItems(items: readonly MenuItem[], emptyMessage?: string): void {
    const screen = this.#currentScreen();
    if (!screen) return;

    this.#screens[this.#screens.length - 1] = {
      ...screen,
      items,
      emptyMessage: emptyMessage ?? screen.emptyMessage,
    };
    this.#index = 0;
    this.#renderList();
  }

  activate(): void {
    const matched = this.#matches[this.#index];
    if (!matched?.item.run) return;

    // `run` may push a screen or close the menu, so it goes last.
    matched.item.run();
  }

  focusFilter(): void {
    this.#filter?.focus();
  }

  // ---- Rendering ----

  #build(): void {
    const panel = document.createElement("div");
    panel.className = "navigation-panel";
    panel.setAttribute("role", "dialog");
    panel.setAttribute("aria-label", "Navigation menu");

    const header = document.createElement("header");
    header.className = "navigation-panel__header";

    const title = document.createElement("span");
    title.className = "navigation-panel__title";
    header.appendChild(title);

    const counter = document.createElement("span");
    counter.className = "navigation-panel__counter";
    header.appendChild(counter);

    panel.appendChild(header);

    const filterRow = document.createElement("div");
    filterRow.className = "navigation-panel__filter-row";

    const prompt = document.createElement("span");
    prompt.className = "navigation-panel__filter-prompt";
    prompt.textContent = "/";
    filterRow.appendChild(prompt);

    const filter = document.createElement("input");
    filter.type = "search";
    filter.id = "navigation-input";
    filter.className = "navigation-panel__filter";
    filter.autocomplete = "off";
    filter.setAttribute("aria-label", "Filter options");
    filterRow.appendChild(filter);

    panel.appendChild(filterRow);

    const body = document.createElement("div");
    body.className = "navigation-panel__body";
    panel.appendChild(body);

    const hints = document.createElement("footer");
    hints.className = "navigation-panel__hints";
    panel.appendChild(hints);

    this.#host.replaceChildren(panel);
    this.#host.hidden = false;
    if (this.#affordance) this.#affordance.hidden = true;

    this.#panel = panel;
    this.#title = title;
    this.#counter = counter;
    this.#body = body;
    this.#filter = filter;
    this.#hints = hints;

    this.#wireFilter(filter);
  }

  /**
   * The filter field owns its keys while focused. Enter activates the selection without
   * leaving the field, and Escape hands control back to the list rather than closing the
   * menu outright — losing a whole screen to a stray Escape is the annoying version.
   */
  #wireFilter(filter: HTMLInputElement): void {
    filter.addEventListener("input", () => {
      this.#filterText = filter.value;
      this.#index = 0;

      const screen = this.#currentScreen();
      if (screen?.onQuery) {
        // The server decides what this screen holds; the rows stand until it answers.
        this.#scheduleQuery(screen.onQuery, filter.value);
        return;
      }

      this.#renderList();
    });

    filter.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        event.preventDefault();
        filter.blur();
        return;
      }

      if (event.key === "Enter") {
        event.preventDefault();
        this.activate();
        return;
      }

      if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        event.preventDefault();
        this.move(event.key === "ArrowDown" ? 1 : -1);
      }
    });
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

  #currentScreen(): MenuScreen | undefined {
    return this.#screens[this.#screens.length - 1];
  }

  #render(): void {
    const screen = this.#currentScreen();
    if (!screen || !this.#filter) return;

    this.#renderTitle();
    this.#filter.value = this.#filterText;
    this.#filter.placeholder = screen.filterPlaceholder ?? "filter…";
    this.#renderList();
    this.#renderHints();
  }

  #renderTitle(): void {
    const title = this.#title;
    if (!title) return;

    title.replaceChildren();

    // Every crumb but the last is dim: "navigation / chapters".
    this.#screens.forEach((screen, position) => {
      const isLast = position === this.#screens.length - 1;
      const crumb = document.createElement("span");
      if (!isLast) crumb.className = "navigation-panel__trail";
      crumb.textContent = isLast ? screen.title : `${screen.title} / `;
      title.appendChild(crumb);
    });
  }

  #renderList(): void {
    const body = this.#body;
    const screen = this.#currentScreen();
    if (!body || !screen) return;

    // A query screen's rows already are the answer; filtering them again would hide results.
    const isQueryScreen = screen.onQuery !== undefined;
    const matched = isQueryScreen
      ? screen.items.map((item) => ({ item, matchStart: -1, matchLength: 0 }))
      : matchItems(screen.items, this.#filterText);

    // Derived rows come from the filter text rather than from the list, so nothing in them
    // is a "match" to highlight.
    const derived = (screen.itemsFromFilter?.(this.#filterText) ?? []).map((item) => ({
      item,
      matchStart: -1,
      matchLength: 0,
    }));

    this.#matches = derived.length > 0 ? [...matched, ...derived] : matched;

    body.replaceChildren();

    if (this.#matches.length === 0) {
      const message = document.createElement("p");
      message.className = "navigation-panel__message";
      message.textContent =
        this.#filterText.length > 0 && !isQueryScreen
          ? `No option matches “${this.#filterText}”.`
          : (screen.emptyMessage ?? "Nothing here yet.");
      body.appendChild(message);
      this.#renderCounter();
      return;
    }

    if (this.#index >= this.#matches.length) this.#index = this.#matches.length - 1;

    const list = document.createElement("ul");
    list.className = "navigation-list";
    list.setAttribute("role", "listbox");

    for (const matched of this.#matches) {
      list.appendChild(buildOption(matched));
    }

    body.appendChild(list);
    this.#applyOverflowFades(list);
    this.#renderSelection();
  }

  /**
   * A row is one line. A label too long for its share of it is faded out over its last
   * couple of characters rather than clipped, so the dim column on the right — a novel's
   * rank and length — stays readable instead of being pushed off the row.
   *
   * Measured rather than guessed: the fade is only worth applying when the text really does
   * overflow, and only the browser knows how wide the text ended up.
   */
  #applyOverflowFades(list: HTMLElement): void {
    for (const option of Array.from(list.children)) {
      const label = option.querySelector<HTMLElement>(".navigation-list__label");
      if (!label) continue;

      // A pixel of slack: sub-pixel text widths make an exactly-fitting label look wider.
      const overflowing = label.scrollWidth - label.clientWidth > 1;
      label.classList.toggle("navigation-list__label--faded", overflowing);
    }
  }

  #renderSelection(): void {
    const list = this.#body?.querySelector(".navigation-list");
    if (!list) return;

    const options = Array.from(list.children);
    options.forEach((option, position) => {
      const isSelected = position === this.#index;
      option.classList.toggle("navigation-list-option-selected", isSelected);
      option.setAttribute("aria-selected", String(isSelected));
      if (isSelected && option instanceof HTMLElement) {
        option.scrollIntoView({ block: "nearest" });
      }
    });

    this.#renderCounter();
  }

  #renderCounter(): void {
    const counter = this.#counter;
    if (!counter) return;

    counter.textContent =
      this.#matches.length === 0 ? "0" : `${this.#index + 1}/${this.#matches.length}`;
  }

  #renderHints(): void {
    const hints = this.#hints;
    if (!hints) return;

    const parts = ["j/k move", "enter select", "/ filter"];
    parts.push(this.#screens.length > 1 ? "esc back" : "esc close");
    hints.textContent = parts.join("  ·  ");
  }
}

/** Rows whose label contains the filter, in screen order. An empty filter matches all. */
/** Clamped so a screen cannot ask for a row it does not have. */
function startingIndexOf(screen: MenuScreen | undefined): number {
  if (!screen) return 0;

  const requested = screen.initialIndex ?? 0;
  return Math.max(0, Math.min(requested, screen.items.length - 1));
}

function matchItems(items: readonly MenuItem[], filterText: string): MatchedItem[] {
  const needle = filterText.trim().toLowerCase();
  if (needle.length === 0) {
    return items.map((item) => ({ item, matchStart: -1, matchLength: 0 }));
  }

  const matched: MatchedItem[] = [];
  for (const item of items) {
    const at = item.label.toLowerCase().indexOf(needle);
    if (at >= 0) matched.push({ item, matchStart: at, matchLength: needle.length });
  }

  return matched;
}

function buildOption(matched: MatchedItem): HTMLElement {
  const option = document.createElement("li");
  option.className = "navigation-list__option";
  option.setAttribute("role", "option");

  const label = document.createElement("span");
  label.className = "navigation-list__label";
  appendLabel(label, matched);
  option.appendChild(label);

  if (matched.item.detail) {
    const detail = document.createElement("span");
    detail.className = "navigation-list__detail";
    detail.textContent = matched.item.detail;
    option.appendChild(detail);
  }

  return option;
}

/** Writes the label, wrapping the matched substring so the filter shows its work. */
function appendLabel(target: HTMLElement, matched: MatchedItem): void {
  const { item, matchStart, matchLength } = matched;
  if (matchStart < 0) {
    target.textContent = item.label;
    return;
  }

  const before = item.label.slice(0, matchStart);
  const hit = item.label.slice(matchStart, matchStart + matchLength);
  const after = item.label.slice(matchStart + matchLength);

  if (before) target.appendChild(document.createTextNode(before));

  const mark = document.createElement("span");
  mark.className = "navigation-list__match";
  mark.textContent = hit;
  target.appendChild(mark);

  if (after) target.appendChild(document.createTextNode(after));
}
