/**
 * The settings drawer: a panel on the left, opened by `c` — or on a phone by the `>` button in
 * the top-left corner — and closed by `c`, by Escape, by the same button now showing `<`, or by
 * tapping the page beside it (D35). It appears at once; the site does not animate.
 *
 * From the keyboard it is a grid (D36). j/k move the cursor from row to row — font size,
 * background, text colour, a row of buttons — and h/l move along a row: from a colour's swatch
 * to its hex field, from `save` to `remove`. On a slider, h/l move the slider itself. Enter
 * edits the field under the cursor, or presses the button; Escape leaves the field and goes
 * back to choosing, and Escape while choosing closes the drawer.
 *
 * The drawer itself knows nothing about any one setting. It is a titled, scrolling column of
 * {@link SettingsSection}s, each of which builds its own rows; adding an option means writing
 * a section and adding it to the list the page passes in. The body scrolls on its own once the
 * sections outgrow the screen, so the list can grow without the panel changing shape.
 */

export interface SettingsSection {
  /** Shown as the section's heading. */
  readonly title: string;
  /** Builds the section's rows into `container`. Called once, when the panel is built. */
  build(container: HTMLElement): void;
  /** Called every time the drawer opens, to bring what it shows up to date. */
  refresh?(): void;
}

export interface SettingsPanelCallbacks {
  readonly onOpen: () => void;
  readonly onClose: () => void;
}

/** Everything the cursor can stop on. */
const controlSelector = "input, select, button";

/** What j/k step between: a captioned row of fields, or a row of buttons. */
const rowSelector = ".settings-row, .settings-actions";

/** Fields that take typing, where Enter means "done" rather than "submit". */
function isTypingField(element: Element): boolean {
  return element instanceof HTMLSelectElement
    || (element instanceof HTMLInputElement && element.type !== "color" && element.type !== "button");
}

export class SettingsPanel {
  readonly #toggle: HTMLButtonElement;
  readonly #sections: readonly SettingsSection[];
  readonly #callbacks: SettingsPanelCallbacks;
  readonly #scrim: HTMLElement;
  readonly #panel: HTMLElement;
  readonly #body: HTMLElement;
  readonly #hints: HTMLElement;
  #open = false;
  /** The control the keyboard cursor is on. */
  #cursor: HTMLElement | undefined;

  constructor(toggle: HTMLButtonElement, sections: readonly SettingsSection[], callbacks: SettingsPanelCallbacks) {
    this.#toggle = toggle;
    this.#sections = sections;
    this.#callbacks = callbacks;

    // The page beside the drawer: dimmed, and a tap on it closes the drawer, as in any
    // phone app's side menu.
    const scrim = document.createElement("div");
    scrim.className = "settings-scrim";
    scrim.hidden = true;
    scrim.addEventListener("click", () => this.close());

    const panel = document.createElement("aside");
    panel.className = "settings-panel";
    panel.id = "settings-panel";
    panel.setAttribute("role", "dialog");
    panel.setAttribute("aria-label", "Settings");
    panel.hidden = true;

    const header = document.createElement("header");
    header.className = "settings-panel__header";
    const title = document.createElement("span");
    title.className = "settings-panel__title";
    title.textContent = "settings";
    header.appendChild(title);
    panel.appendChild(header);

    const body = document.createElement("div");
    body.className = "settings-panel__body";
    panel.appendChild(body);

    const hints = document.createElement("footer");
    hints.className = "settings-panel__hints";
    panel.appendChild(hints);

    for (const section of sections) {
      const element = document.createElement("section");
      element.className = "settings-section";

      const heading = document.createElement("h2");
      heading.className = "settings-section__title";
      heading.textContent = section.title;
      element.appendChild(heading);

      section.build(element);
      body.appendChild(element);
    }

    // Keys inside a field. The router leaves text fields alone, so leaving one is handled
    // here: Escape always, and Enter in anything typed into. The router then has the keys back
    // and j/k move the cursor again.
    panel.addEventListener("keydown", (event) => {
      if (event.defaultPrevented) return;

      const target = event.target;
      if (!(target instanceof HTMLElement) || !target.matches(controlSelector)) return;

      const leaving = event.key === "Escape" || (event.key === "Enter" && isTypingField(target));
      if (!leaving) return;

      event.preventDefault();
      // Not the router's Escape too, which would close the whole drawer.
      event.stopPropagation();
      target.blur();
    });

    // A tap or a click puts the cursor where the reader is working, so the keyboard carries
    // on from there; leaving a field puts the hints back to choosing.
    panel.addEventListener("focusin", (event) => {
      if (event.target instanceof HTMLElement && event.target.matches(controlSelector)) {
        this.#setCursor(event.target);
      }
      this.#renderHints();
    });
    panel.addEventListener("focusout", () => queueMicrotask(() => this.#renderHints()));

    document.body.append(scrim, panel);

    this.#scrim = scrim;
    this.#panel = panel;
    this.#body = body;
    this.#hints = hints;

    toggle.setAttribute("aria-controls", panel.id);
    toggle.addEventListener("click", () => this.toggle());
    this.#renderToggle();
  }

  get isOpen(): boolean {
    return this.#open;
  }

  toggle(): void {
    if (this.#open) this.close();
    else this.open();
  }

  open(): void {
    if (this.#open) return;
    this.#open = true;

    for (const section of this.#sections) section.refresh?.();

    this.#scrim.hidden = false;
    this.#panel.hidden = false;
    document.documentElement.classList.add("settings-open");

    this.#setCursor(this.#controls()[0]);
    this.#renderHints();
    this.#renderToggle();
    this.#callbacks.onOpen();
  }

  close(): void {
    if (!this.#open) return;
    this.#open = false;

    // A focused field would otherwise keep the soft keyboard up over the page.
    if (document.activeElement instanceof HTMLElement && this.#panel.contains(document.activeElement)) {
      document.activeElement.blur();
    }

    document.documentElement.classList.remove("settings-open");
    this.#scrim.hidden = true;
    this.#panel.hidden = true;

    this.#renderToggle();
    this.#callbacks.onClose();
  }

  /**
   * j and k: the next or previous row, landing on its first control. Rows with nothing enabled
   * in them — `remove` before anything is saved — are skipped. Wraps.
   */
  move(step: 1 | -1): void {
    const rows = this.#rows();
    if (rows.length === 0) return;

    const currentRow = this.#cursor?.closest(rowSelector);
    const current = rows.findIndex((row) => row === currentRow);
    const next = current < 0
      ? (step > 0 ? 0 : rows.length - 1)
      : (current + step + rows.length) % rows.length;

    this.#setCursor(this.#controlsIn(rows[next]!)[0]);
  }

  /**
   * h and l: along the row. On a slider they move the slider — one step, exactly as an arrow
   * key would once inside it — so the font size needs no Enter first.
   */
  across(step: 1 | -1): void {
    const control = this.#cursor;
    if (!control) return;

    if (control instanceof HTMLInputElement && control.type === "range") {
      if (step > 0) control.stepUp();
      else control.stepDown();
      // stepUp/stepDown change the value silently; the section listens for `input`.
      control.dispatchEvent(new Event("input", { bubbles: true }));
      return;
    }

    const row = control.closest(rowSelector);
    if (!row) return;

    const controls = this.#controlsIn(row);
    const index = controls.indexOf(control);
    const next = controls[index + step];
    if (next) this.#setCursor(next);
  }

  /**
   * Enter: press the button under the cursor, or go into the field to edit it. The colour
   * swatch is pressed rather than entered, which opens the browser's picker.
   */
  activate(): void {
    const control = this.#cursor;
    if (!control || !control.isConnected || (control as HTMLButtonElement).disabled) return;

    if (control instanceof HTMLButtonElement || (control instanceof HTMLInputElement && control.type === "color")) {
      control.click();
      return;
    }

    control.focus();
    if (control instanceof HTMLInputElement && control.type !== "range") control.select();
  }

  /** In document order, so j/k follow the drawer down the page. */
  #rows(): Element[] {
    return [...this.#body.querySelectorAll(rowSelector)]
      .filter((row) => this.#controlsIn(row).length > 0);
  }

  #controlsIn(row: Element): HTMLElement[] {
    return [...row.querySelectorAll<HTMLElement>(controlSelector)]
      .filter((control) => !(control as HTMLButtonElement).disabled);
  }

  #controls(): HTMLElement[] {
    return this.#rows().flatMap((row) => this.#controlsIn(row));
  }

  #setCursor(control: HTMLElement | undefined): void {
    this.#cursor?.classList.remove("settings-cursor");
    this.#cursor?.closest(".settings-row")?.classList.remove("settings-row--cursor");

    this.#cursor = control;
    if (!control) return;

    control.classList.add("settings-cursor");
    control.closest(".settings-row")?.classList.add("settings-row--cursor");
    // `nearest`, so a control already in view does not make the drawer jump.
    control.scrollIntoView({ block: "nearest" });
    this.#renderHints();
  }

  #renderHints(): void {
    const editing = document.activeElement instanceof HTMLElement
      && this.#panel.contains(document.activeElement)
      && document.activeElement.matches(controlSelector);

    if (editing) {
      this.#hints.textContent = "esc done";
      return;
    }

    // h/l is named for what it does where the cursor is, and left out where it does nothing.
    const control = this.#cursor;
    const row = control?.closest(rowSelector);
    const across = control instanceof HTMLInputElement && control.type === "range"
      ? "h/l smaller/larger"
      : row && this.#controlsIn(row).length > 1 ? "h/l across" : undefined;

    this.#hints.textContent = ["j/k rows", across, "enter edit", "esc close"]
      .filter((part) => part !== undefined)
      .join("  ·  ");
  }

  /** `>` to open, `<` to close: the glyph points the way the drawer will move. */
  #renderToggle(): void {
    const glyph = this.#toggle.querySelector(".settings-affordance__glyph");
    if (glyph) glyph.textContent = this.#open ? "<" : ">";

    this.#toggle.setAttribute("aria-expanded", String(this.#open));
    this.#toggle.setAttribute("aria-label", this.#open ? "Close settings" : "Open settings");
  }
}

// ---- Building blocks for sections ----
// Shared so that every section's rows line up and read the same way.

/** A captioned row: the label above, the field (or fields) under it. */
export function settingsRow(caption: string, ...fields: HTMLElement[]): HTMLLabelElement {
  const row = document.createElement("label");
  row.className = "settings-row";

  const label = document.createElement("span");
  label.className = "settings-row__label";
  label.textContent = caption;
  row.appendChild(label);

  const controls = document.createElement("span");
  controls.className = "settings-row__controls";
  controls.append(...fields);
  row.appendChild(controls);

  return row;
}

export function settingsButton(label: string, run: () => void): HTMLButtonElement {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "settings-button";
  button.textContent = label;
  button.addEventListener("click", run);
  return button;
}

/** A row of buttons, right-aligned under a section's fields. */
export function settingsActions(...buttons: HTMLButtonElement[]): HTMLElement {
  const actions = document.createElement("div");
  actions.className = "settings-actions";
  actions.append(...buttons);
  return actions;
}

/** A line of dim text — a section's status, or the reason a save was refused. */
export function settingsNote(): HTMLParagraphElement {
  const note = document.createElement("p");
  note.className = "settings-note";
  note.setAttribute("role", "status");
  return note;
}
