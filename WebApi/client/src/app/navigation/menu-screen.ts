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
