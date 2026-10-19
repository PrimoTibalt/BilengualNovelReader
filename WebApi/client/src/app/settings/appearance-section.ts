import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from "@angular/core";
import {
  type AppearancePreferences,
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
} from "./appearance";
import { SettingsPanel } from "./settings-panel";

type ScaleKey = "fontScale" | "titleScale";
type ColourKey = "background" | "text";

/**
 * Font size, chapter title size, background and text colour. Every change is applied as it is
 * made and stored at once — there is no save button, because there is nothing a reader would
 * want to type and then not see. `reset` puts all four back to the stylesheet's defaults and
 * forgets them.
 */
@Component({
  selector: "app-appearance-section",
  templateUrl: "./appearance-section.html",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppearanceSection {
  /**
   * Wraps every change of font size. The page notes the paragraph the reader has at the
   * bottom of the screen, lets the change happen, and puts that paragraph back at the bottom —
   * every line below the top of the chapter moves when the font does.
   */
  readonly keepReadingPosition = input.required<(change: () => void) => void>();

  protected readonly preferences = signal<AppearancePreferences>(loadAppearance());
  /** Hex fields as typed — half-typed is not wrong yet, and is not applied until it is a colour. */
  protected readonly typed = signal<Record<ColourKey, string>>({ background: "", text: "" });
  protected readonly note = signal("");

  protected readonly minimumFontScale = minimumFontScale;
  protected readonly maximumFontScale = maximumFontScale;
  protected readonly fontScaleStep = fontScaleStep;
  protected readonly fallbacks: Record<ColourKey, string> = { background: defaultBackground, text: defaultText };

  protected readonly scaleRows: readonly { key: ScaleKey; caption: string }[] = [
    { key: "fontScale", caption: "font size" },
    { key: "titleScale", caption: "chapter title size" },
  ];

  protected readonly colourRows: readonly { key: ColourKey; caption: string }[] = [
    { key: "background", caption: "background" },
    { key: "text", caption: "text colour" },
  ];

  constructor() {
    applyAppearance(this.preferences());

    const panel = inject(SettingsPanel);
    effect(() => {
      panel.openings();
      untracked(() => this.#refresh());
    });
  }

  protected percent(key: ScaleKey): string {
    return `${Math.round((this.preferences()[key] ?? 1) * 100)}%`;
  }

  /** Every size change goes through the page's `keepReadingPosition`, the title's included. */
  protected onScale(key: ScaleKey, value: string): void {
    const chosen = Number(value);
    // Back at 100% is the default, not a preference that happens to equal it.
    this.keepReadingPosition()(() =>
      this.#update({ [key]: Math.abs(chosen - 1) < fontScaleStep / 2 ? null : chosen }));
  }

  protected isInvalid(key: ColourKey): boolean {
    const typed = this.typed()[key].trim();
    return typed.length > 0 && normalizeHex(typed) === undefined;
  }

  protected swatchValue(key: ColourKey): string {
    return this.preferences()[key] ?? this.fallbacks[key];
  }

  /** Emptying the field is how one colour alone goes back to its default. */
  protected onHex(key: ColourKey, value: string): void {
    this.typed.update((typed) => ({ ...typed, [key]: value }));

    const typed = value.trim();
    if (typed.length === 0) {
      this.#update({ [key]: null });
      return;
    }

    const hex = normalizeHex(typed);
    if (hex !== undefined) this.#update({ [key]: hex });
  }

  protected onHexBlur(key: ColourKey): void {
    if (this.isInvalid(key)) this.note.set(`“${this.typed()[key].trim()}” is not a colour — use #rrggbb.`);
  }

  protected onSwatch(key: ColourKey, value: string): void {
    this.typed.update((typed) => ({ ...typed, [key]: value }));
    this.#update({ [key]: value });
  }

  protected reset(): void {
    this.keepReadingPosition()(() => this.#update(defaultAppearance));
    this.#refresh();
  }

  #refresh(): void {
    const preferences = loadAppearance();
    this.preferences.set(preferences);
    this.typed.set({ background: preferences.background ?? "", text: preferences.text ?? "" });
    this.note.set("");
  }

  #update(change: Partial<AppearancePreferences>): void {
    const preferences = { ...this.preferences(), ...change };
    this.preferences.set(preferences);
    applyAppearance(preferences);
    saveAppearance(preferences);
    this.note.set("");
  }
}
