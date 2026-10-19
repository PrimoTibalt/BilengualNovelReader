import {
  ChangeDetectionStrategy,
  Component,
  type ElementRef,
  computed,
  output,
  signal,
  viewChild,
} from "@angular/core";

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
 * The drawer itself knows nothing about any one setting. Its sections are projected into it
 * by the page, each building its own `.settings-row`s and `.settings-actions`, and the grid
 * works from those classes alone — so a new section gets the keys for nothing. A section that
 * shows stored state reads {@link openings} to refresh itself whenever the drawer opens.
 */

/** Everything the cursor can stop on. */
const controlSelector = "input, select, button, textarea";

/** What j/k step between: a captioned row of fields, or a row of buttons. */
const rowSelector = ".settings-row, .settings-actions";

/** Fields that take typing, where Enter means "done" rather than "submit". */
function isTypingField(element: Element): boolean {
  return element instanceof HTMLSelectElement
    || (element instanceof HTMLInputElement && element.type !== "color" && element.type !== "button");
}

@Component({
  selector: "app-settings-panel",
  templateUrl: "./settings-panel.html",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SettingsPanel {
  readonly opened = output<void>();
  readonly closed = output<void>();

  readonly #open = signal(false);
  readonly isOpen = this.#open.asReadonly();

  /** Counts the times the drawer has opened; sections refresh when it moves. */
  readonly #openings = signal(0);
  readonly openings = this.#openings.asReadonly();

  /** Whether a control inside has focus, i.e. the reader is editing rather than choosing. */
  protected readonly editing = signal(false);
  /** The control the keyboard cursor is on. */
  readonly #cursor = signal<HTMLElement | undefined>(undefined);

  protected readonly panel = viewChild.required<ElementRef<HTMLElement>>("panel");
  protected readonly body = viewChild.required<ElementRef<HTMLElement>>("body");

  protected readonly hints = computed(() => {
    if (this.editing()) return "esc done";

    // h/l is named for what it does where the cursor is, and left out where it does nothing.
    const control = this.#cursor();
    const row = control?.closest(rowSelector);
    const across = control instanceof HTMLInputElement && control.type === "range"
      ? "h/l smaller/larger"
      : row && this.#controlsIn(row).length > 1 ? "h/l across" : undefined;

    return ["j/k rows", across, "enter edit", "esc close"]
      .filter((part) => part !== undefined)
      .join("  ·  ");
  });

  toggle(): void {
    if (this.#open()) this.close();
    else this.open();
  }

  open(): void {
    if (this.#open()) return;
    this.#open.set(true);
    this.#openings.update((count) => count + 1);

    document.documentElement.classList.add("settings-open");

    // The sections refresh as the drawer opens, so the cursor waits for them to be drawn.
    queueMicrotask(() => this.#setCursor(this.#controls()[0]));
    this.opened.emit();
  }

  close(): void {
    if (!this.#open()) return;
    this.#open.set(false);

    // A focused field would otherwise keep the soft keyboard up over the page.
    const active = document.activeElement;
    if (active instanceof HTMLElement && this.panel().nativeElement.contains(active)) active.blur();

    document.documentElement.classList.remove("settings-open");
    this.closed.emit();
  }

  /**
   * j and k: the next or previous row, landing on its first control. Rows with nothing enabled
   * in them — `remove` before anything is saved — are skipped. Wraps.
   */
  move(step: 1 | -1): void {
    const rows = this.#rows();
    if (rows.length === 0) return;

    const currentRow = this.#cursor()?.closest(rowSelector);
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
    const control = this.#cursor();
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
    const next = controls[controls.indexOf(control) + step];
    if (next) this.#setCursor(next);
  }

  /**
   * Enter: press the button under the cursor, or go into the field to edit it. The colour
   * swatch is pressed rather than entered, which opens the browser's picker.
   */
  activate(): void {
    const control = this.#cursor();
    if (!control || !control.isConnected || (control as HTMLButtonElement).disabled) return;

    if (control instanceof HTMLButtonElement || (control instanceof HTMLInputElement && control.type === "color")) {
      control.click();
      return;
    }

    control.focus();
    if (control instanceof HTMLInputElement && control.type !== "range") control.select();
  }

  /**
   * Keys inside a field. The router leaves text fields alone, so leaving one is handled here:
   * Escape always, and Enter in anything typed into. The router then has the keys back and j/k
   * move the cursor again.
   */
  protected onKeyDown(event: KeyboardEvent): void {
    if (event.defaultPrevented) return;

    const target = event.target;
    if (!(target instanceof HTMLElement) || !target.matches(controlSelector)) return;

    const leaving = event.key === "Escape" || (event.key === "Enter" && isTypingField(target));
    if (!leaving) return;

    event.preventDefault();
    // Not the router's Escape too, which would close the whole drawer.
    event.stopPropagation();
    target.blur();
  }

  /**
   * A tap or a click puts the cursor where the reader is working, so the keyboard carries on
   * from there; leaving a field puts the hints back to choosing.
   */
  protected onFocusIn(event: FocusEvent): void {
    if (event.target instanceof HTMLElement && event.target.matches(controlSelector)) {
      this.#setCursor(event.target);
    }
    this.#renderEditing();
  }

  protected onFocusOut(): void {
    queueMicrotask(() => this.#renderEditing());
  }

  #renderEditing(): void {
    const active = document.activeElement;
    this.editing.set(active instanceof HTMLElement
      && this.panel().nativeElement.contains(active)
      && active.matches(controlSelector));
  }

  /** In document order, so j/k follow the drawer down the page. */
  #rows(): Element[] {
    return [...this.body().nativeElement.querySelectorAll(rowSelector)]
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
    const previous = this.#cursor();
    previous?.classList.remove("settings-cursor");
    previous?.closest(".settings-row")?.classList.remove("settings-row--cursor");

    this.#cursor.set(control);
    if (!control) return;

    control.classList.add("settings-cursor");
    control.closest(".settings-row")?.classList.add("settings-row--cursor");
    // `nearest`, so a control already in view does not make the drawer jump.
    control.scrollIntoView({ block: "nearest" });
  }
}
