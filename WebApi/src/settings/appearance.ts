/**
 * How the reading column looks: its font size and its two colours.
 *
 * Kept per device, in `localStorage`, not on the account (D35). A size that suits a phone held
 * at arm's length is wrong on a desktop monitor, and the page should paint in the reader's
 * colours before it has spoken to the server at all — index.html applies them from the same
 * key in a script in its <head>, so a light page never flashes dark first.
 *
 * Every field is null when it is at its default, and the default is whatever the stylesheet
 * says. Clearing a field therefore *is* resetting it: nothing here restates the defaults the
 * CSS already owns, except as what the form shows.
 */

export interface AppearancePreferences {
  /** Multiplies the column's font size; 1 is the stylesheet's size. */
  readonly fontScale: number | null;
  /** `#rrggbb`. */
  readonly background: string | null;
  readonly text: string | null;
}

/** Read by the inline script in index.html as well — keep the two in step. */
const storageKey = "novelreader.appearance";

export const defaultAppearance: AppearancePreferences = { fontScale: null, background: null, text: null };

/** What the form shows for a field left at its default. Mirror realTimeReader.css. */
export const defaultBackground = "#1b1b1b";
export const defaultText = "#b2b2b2";

export const minimumFontScale = 0.6;
export const maximumFontScale = 2;
export const fontScaleStep = 0.05;

/**
 * `#abc`, `abc`, `#aabbcc` or `aabbcc`, as `#aabbcc`; undefined for anything else. The short
 * form is accepted because it is what people type.
 */
export function normalizeHex(value: string): string | undefined {
  const match = /^#?([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(value.trim());
  if (!match?.[1]) return undefined;

  const digits = match[1].toLowerCase();
  return digits.length === 3
    ? `#${[...digits].map((digit) => digit + digit).join("")}`
    : `#${digits}`;
}

function clampScale(value: unknown): number | null {
  if (typeof value !== "number" || !Number.isFinite(value)) return null;
  return Math.min(maximumFontScale, Math.max(minimumFontScale, value));
}

function hexOrNull(value: unknown): string | null {
  return typeof value === "string" ? normalizeHex(value) ?? null : null;
}

/** What is stored, with anything unreadable treated as the default rather than as an error. */
export function loadAppearance(): AppearancePreferences {
  try {
    const raw = localStorage.getItem(storageKey);
    if (!raw) return defaultAppearance;

    const parsed = JSON.parse(raw) as Record<string, unknown>;
    return {
      fontScale: clampScale(parsed["fontScale"]),
      background: hexOrNull(parsed["background"]),
      text: hexOrNull(parsed["text"]),
    };
  } catch {
    // Private windows and blocked storage throw; the reader simply gets the defaults.
    return defaultAppearance;
  }
}

/** Stores the preferences, or removes the entry entirely once everything is back at default. */
export function saveAppearance(preferences: AppearancePreferences): void {
  const isDefault = preferences.fontScale === null && preferences.background === null && preferences.text === null;

  try {
    if (isDefault) {
      localStorage.removeItem(storageKey);
    } else {
      localStorage.setItem(storageKey, JSON.stringify(preferences));
    }
  } catch {
    // Applied for this visit regardless; it just will not be remembered.
  }
}

/**
 * Sets the custom properties realTimeReader.css reads. A null field removes its property, so
 * the stylesheet's own default comes back.
 */
export function applyAppearance(preferences: AppearancePreferences): void {
  const style = document.documentElement.style;

  const set = (name: string, value: string | null): void => {
    if (value === null) style.removeProperty(name);
    else style.setProperty(name, value);
  };

  set("--reader-font-scale", preferences.fontScale === null ? null : String(preferences.fontScale));
  set("--reader-background", preferences.background);
  set("--reader-text", preferences.text);
}
