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
 * Font size, background and text colour. Every change is applied as it is made and stored at
 * once — there is no save button, because there is nothing a reader would want to type and
 * then not see. `reset` puts all three back to the stylesheet's defaults and forgets them.
 */
export class AppearanceSection implements SettingsSection {
  readonly title = "appearance";

  readonly #onFontScaleChanged: () => void;
  #preferences: AppearancePreferences = loadAppearance();
  #scale: HTMLInputElement | undefined;
  #scaleValue: HTMLElement | undefined;
  #colourFields: { refresh(): void }[] = [];
  #note: HTMLElement | undefined;

  /**
   * `onFontScaleChanged` runs after the column has been re-set at a new size, so the page can
   * put the reader back on the paragraph they were reading — every line below the top of the
   * chapter moves when the font does.
   */
  constructor(onFontScaleChanged: () => void) {
    this.#onFontScaleChanged = onFontScaleChanged;
    applyAppearance(this.#preferences);
  }

  build(container: HTMLElement): void {
    // ---- font size ---------------------------------------------------------------
    const scale = document.createElement("input");
    scale.type = "range";
    scale.className = "settings-slider";
    scale.min = String(minimumFontScale);
    scale.max = String(maximumFontScale);
    scale.step = String(fontScaleStep);
    scale.setAttribute("aria-label", "Font size");

    const value = document.createElement("span");
    value.className = "settings-slider__value";

    scale.addEventListener("input", () => {
      const chosen = Number(scale.value);
      // Back at 100% is the default, not a preference that happens to equal it.
      this.#update({ fontScale: Math.abs(chosen - 1) < fontScaleStep / 2 ? null : chosen });
      this.#onFontScaleChanged();
    });

    container.appendChild(settingsRow("font size", scale, value));
    this.#scale = scale;
    this.#scaleValue = value;

    // ---- colours -----------------------------------------------------------------
    container.appendChild(this.#colourRow("background", "background", defaultBackground));
    container.appendChild(this.#colourRow("text colour", "text", defaultText));

    const note = settingsNote();
    container.appendChild(note);
    this.#note = note;

    container.appendChild(settingsActions(
      settingsButton("reset to defaults", () => {
        this.#update(defaultAppearance);
        this.#onFontScaleChanged();
        this.refresh();
      }),
    ));

    this.refresh();
  }

  refresh(): void {
    this.#preferences = loadAppearance();

    const scale = this.#preferences.fontScale ?? 1;
    if (this.#scale) this.#scale.value = String(scale);
    this.#renderScale(scale);

    for (const field of this.#colourFields) field.refresh();
    this.#showNote("");
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
    this.#renderScale(this.#preferences.fontScale ?? 1);
    this.#showNote("");
  }

  #renderScale(scale: number): void {
    if (this.#scaleValue) this.#scaleValue.textContent = `${Math.round(scale * 100)}%`;
  }

  #showNote(text: string): void {
    if (this.#note) this.#note.textContent = text;
  }
}
