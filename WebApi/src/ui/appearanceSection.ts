import {
  applyAppearance,
  defaultAppearance,
  defaultBackground,
  defaultText,
  fontScaleStep,
  loadAppearance,
  maximumFontScale,
  minimumFontScale,
  normalizeHex,
  saveAppearance,
  type AppearancePreferences,
} from "../settings/appearance.js";
import { settingsActions, settingsButton, settingsNote, settingsRow, type SettingsSection } from "./settingsPanel.js";

/**
 * Font size, chapter title size, background and text colour. Every change is applied as it is made and stored at
 * once — there is no save button, because there is nothing a reader would want to type and
 * then not see. `reset` puts all four back to the stylesheet's defaults and forgets them.
 */
export class AppearanceSection implements SettingsSection {
  readonly title = "appearance";

  readonly #keepReadingPosition: (change: () => void) => void;
  #preferences: AppearancePreferences = loadAppearance();
  #scaleFields: { refresh(): void }[] = [];
  #colourFields: { refresh(): void }[] = [];
  #note: HTMLElement | undefined;

  /**
   * `keepReadingPosition` wraps every change of font size. The page notes the paragraph the
   * reader has at the bottom of the screen, lets the change happen, and puts that paragraph
   * back at the bottom — every line below the top of the chapter moves when the font does.
   */
  constructor(keepReadingPosition: (change: () => void) => void) {
    this.#keepReadingPosition = keepReadingPosition;
    applyAppearance(this.#preferences);
  }

  build(container: HTMLElement): void {
    // ---- sizes -------------------------------------------------------------------
    // Two sliders, each independent of the other (D38): the text, and the chapter titles.
    container.appendChild(this.#scaleRow("font size", "fontScale"));
    container.appendChild(this.#scaleRow("chapter title size", "titleScale"));

    // ---- colours -----------------------------------------------------------------
    container.appendChild(this.#colourRow("background", "background", defaultBackground));
    container.appendChild(this.#colourRow("text colour", "text", defaultText));

    const note = settingsNote();
    container.appendChild(note);
    this.#note = note;

    container.appendChild(settingsActions(
      settingsButton("reset to defaults", () => {
        this.#keepReadingPosition(() => this.#update(defaultAppearance));
        this.refresh();
      }),
    ));

    this.refresh();
  }

  refresh(): void {
    this.#preferences = loadAppearance();

    for (const field of this.#scaleFields) field.refresh();
    for (const field of this.#colourFields) field.refresh();
    this.#showNote("");
  }

  /**
   * A size slider with its percentage beside it. Every change goes through the page's
   * `keepReadingPosition`, the title's included: a bigger title pushes the chapter down too.
   */
  #scaleRow(caption: string, key: "fontScale" | "titleScale"): HTMLElement {
    const scale = document.createElement("input");
    scale.type = "range";
    scale.className = "settings-slider";
    scale.min = String(minimumFontScale);
    scale.max = String(maximumFontScale);
    scale.step = String(fontScaleStep);
    scale.setAttribute("aria-label", caption);

    const value = document.createElement("span");
    value.className = "settings-slider__value";

    const render = (): void => {
      value.textContent = `${Math.round((this.#preferences[key] ?? 1) * 100)}%`;
    };

    scale.addEventListener("input", () => {
      const chosen = Number(scale.value);
      // Back at 100% is the default, not a preference that happens to equal it.
      this.#keepReadingPosition(() =>
        this.#update({ [key]: Math.abs(chosen - 1) < fontScaleStep / 2 ? null : chosen }));
      render();
    });

    this.#scaleFields.push({
      refresh: () => {
        scale.value = String(this.#preferences[key] ?? 1);
        render();
      },
    });

    return settingsRow(caption, scale, value);
  }

  /**
   * A hex field with a swatch beside it. The swatch is the browser's colour picker; the field
   * is what a reader who knows the colour they want types into. Each follows the other.
   * Emptying the field is how one colour alone goes back to its default.
   */
  #colourRow(caption: string, key: "background" | "text", fallback: string): HTMLElement {
    const swatch = document.createElement("input");
    swatch.type = "color";
    swatch.className = "settings-swatch";
    swatch.setAttribute("aria-label", `${caption} picker`);

    const field = document.createElement("input");
    field.type = "text";
    field.className = "settings-field settings-field--hex";
    field.placeholder = fallback;
    field.maxLength = 7;
    field.spellcheck = false;
    field.autocomplete = "off";
    field.setAttribute("autocapitalize", "off");
    field.setAttribute("aria-label", `${caption}, hex`);

    field.addEventListener("input", () => {
      const typed = field.value.trim();
      if (typed.length === 0) {
        field.classList.remove("settings-field--invalid");
        swatch.value = fallback;
        this.#update({ [key]: null });
        return;
      }

      const hex = normalizeHex(typed);
      // Half-typed is not wrong yet: it is marked, and applied once it is a colour.
      field.classList.toggle("settings-field--invalid", hex === undefined);
      if (hex === undefined) return;

      swatch.value = hex;
      this.#update({ [key]: hex });
    });

    field.addEventListener("blur", () => {
      if (field.value.trim().length > 0 && normalizeHex(field.value) === undefined) {
        this.#showNote(`“${field.value.trim()}” is not a colour — use #rrggbb.`);
      }
    });

    swatch.addEventListener("input", () => {
      field.value = swatch.value;
      field.classList.remove("settings-field--invalid");
      this.#update({ [key]: swatch.value });
    });

    this.#colourFields.push({
      refresh: () => {
        const stored = this.#preferences[key];
        field.value = stored ?? "";
        field.classList.remove("settings-field--invalid");
        swatch.value = stored ?? fallback;
      },
    });

    return settingsRow(caption, swatch, field);
  }

  #update(change: Partial<AppearancePreferences>): void {
    this.#preferences = { ...this.#preferences, ...change };
    applyAppearance(this.#preferences);
    saveAppearance(this.#preferences);
    this.#showNote("");
  }

  #showNote(text: string): void {
    if (this.#note) this.#note.textContent = text;
  }
}
