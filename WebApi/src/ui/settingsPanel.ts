/**
 * The settings drawer: a panel that slides in from the left, opened by the `>` button in the
 * top-left corner or by `c`, and closed by the same button — now showing `<` — by `c` or
 * Escape, or by tapping the page beside it (D35).
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

/** How far one press of j or k moves the drawer's body. */
const scrollStepInPixels = 80;

export class SettingsPanel {
  readonly #toggle: HTMLButtonElement;
  readonly #sections: readonly SettingsSection[];
  readonly #callbacks: SettingsPanelCallbacks;
  readonly #scrim: HTMLElement;
  readonly #panel: HTMLElement;
  readonly #body: HTMLElement;
  #open = false;

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
    hints.textContent = "j/k scroll  ·  c/esc close";
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

    // Escape from inside a field: the key router leaves text fields alone, so the drawer
    // listens for itself. A field's own Escape handling, if it has any, runs first.
    panel.addEventListener("keydown", (event) => {
      if (event.key !== "Escape" || event.defaultPrevented) return;
      event.preventDefault();
      this.close();
    });

    document.body.append(scrim, panel);

    this.#scrim = scrim;
    this.#panel = panel;
    this.#body = body;

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
    // The slide needs a laid-out starting point, so layout is forced before the class goes on.
    // Not a `requestAnimationFrame`: that never fires in a background tab, and the drawer
    // would stay parked off screen (D23).
    void this.#panel.offsetWidth;
    document.documentElement.classList.add("settings-open");

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

  /** j and k, while the drawer has the keyboard. */
  scroll(direction: 1 | -1): void {
    this.#body.scrollBy({ top: direction * scrollStepInPixels, behavior: "smooth" });
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
