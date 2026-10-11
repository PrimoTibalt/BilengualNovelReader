import type { PasswordChangeResult } from "../reading/connection.js";
import { settingsActions, settingsButton, settingsNote, settingsRow, type SettingsSection } from "./settingsPanel.js";

/** Undefined when the server could not be reached. */
export type ChangePassword = (currentPassword: string, newPassword: string) => Promise<PasswordChangeResult | undefined>;

/** The server's limits; checked here only to save a round trip. */
const minimumPasswordLength = 8;
const maximumPasswordLength = 128;

const svgNamespace = "http://www.w3.org/2000/svg";

/** An open eye, or — `struck` — the same eye with a line through it. */
function eyeIcon(struck: boolean): SVGSVGElement {
  const svg = document.createElementNS(svgNamespace, "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("fill", "none");
  svg.setAttribute("stroke", "currentColor");
  svg.setAttribute("stroke-width", "2");
  svg.setAttribute("stroke-linecap", "round");
  svg.setAttribute("stroke-linejoin", "round");
  svg.setAttribute("aria-hidden", "true");

  const shapes: [string, Record<string, string>][] = [
    ["path", { d: "M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z" }],
    ["circle", { cx: "12", cy: "12", r: "3" }],
  ];
  if (struck) shapes.push(["line", { x1: "3", y1: "3", x2: "21", y2: "21" }]);

  for (const [name, attributes] of shapes) {
    const shape = document.createElementNS(svgNamespace, name);
    for (const [key, value] of Object.entries(attributes)) shape.setAttribute(key, value);
    svg.appendChild(shape);
  }
  return svg;
}

function describeError(code: string, retryAfterMinutes: number | null = null): string {
  switch (code) {
    case "locked":
      return retryAfterMinutes === null || retryAfterMinutes <= 1
        ? "Too many wrong passwords. Try again in a minute."
        : `Too many wrong passwords. Try again in ${retryAfterMinutes} minutes.`;
    case "too-short":
      return `Passwords must be at least ${minimumPasswordLength} characters.`;
    case "too-long":
      return `Passwords can be at most ${maximumPasswordLength} characters.`;
    case "current-wrong":
      return "The current password is not right.";
    default:
      return "Could not change the password — try again.";
  }
}

/** A password field with an eye beside it that shows and hides what was typed. */
class RevealableField {
  readonly field: HTMLInputElement;
  readonly reveal: HTMLButtonElement;

  constructor(label: string, autocomplete: AutoFill, placeholder: string) {
    const field = document.createElement("input");
    field.type = "password";
    field.className = "settings-field";
    field.spellcheck = false;
    field.autocomplete = autocomplete;
    field.placeholder = placeholder;
    field.setAttribute("autocapitalize", "off");
    field.setAttribute("aria-label", label);
    this.field = field;

    const reveal = document.createElement("button");
    reveal.type = "button";
    reveal.className = "settings-reveal";
    // Keeps the focus — and a phone's keyboard — in the field when the eye is tapped.
    reveal.addEventListener("mousedown", (event) => event.preventDefault());
    reveal.addEventListener("click", () => this.setRevealed(field.type === "password"));
    this.reveal = reveal;

    this.setRevealed(false);
  }

  /** Empty and hidden, as it is whenever the drawer opens. */
  clear(): void {
    this.field.value = "";
    this.setRevealed(false);
  }

  setRevealed(revealed: boolean): void {
    this.field.type = revealed ? "text" : "password";
    this.reveal.replaceChildren(eyeIcon(revealed));
    this.reveal.setAttribute("aria-pressed", String(revealed));
    this.reveal.setAttribute("aria-label", revealed ? "Hide password" : "Show password");
    this.reveal.title = revealed ? "hide password" : "show password";
  }
}

/**
 * Changes the signed-in reader's password (D40). The current password is required, so a
 * session left open on someone else's screen cannot be turned into the account. The new one
 * is typed once: instead of a second field to repeat it in, the eye beside it shows what was
 * typed. Both fields are cleared and hidden again every time the drawer opens, so a revealed
 * password is not left on screen.
 */
export class PasswordSection implements SettingsSection {
  readonly title = "password";

  readonly #changePassword: ChangePassword;
  #current: RevealableField | undefined;
  #next: RevealableField | undefined;
  #note: HTMLElement | undefined;
  #save: HTMLButtonElement | undefined;
  #busy = false;

  constructor(changePassword: ChangePassword) {
    this.#changePassword = changePassword;
  }

  build(container: HTMLElement): void {
    const current = new RevealableField("Current password", "current-password", "");
    container.appendChild(settingsRow("current password", current.field, current.reveal));
    this.#current = current;

    const next = new RevealableField("New password", "new-password", `at least ${minimumPasswordLength} characters`);
    container.appendChild(settingsRow("new password", next.field, next.reveal));
    this.#next = next;

    const note = settingsNote();
    container.appendChild(note);
    this.#note = note;

    this.#save = settingsButton("change", () => void this.#submit());
    container.appendChild(settingsActions(this.#save));

    this.refresh();
  }

  refresh(): void {
    this.#current?.clear();
    this.#next?.clear();
    this.#showNote("");
  }

  async #submit(): Promise<void> {
    const current = this.#current;
    const next = this.#next;
    if (this.#busy || !current || !next) return;

    if (current.field.value.length === 0) {
      this.#showNote("Enter your current password.");
      current.field.focus();
      return;
    }
    if (next.field.value.length < minimumPasswordLength || next.field.value.length > maximumPasswordLength) {
      this.#showNote(describeError(next.field.value.length < minimumPasswordLength ? "too-short" : "too-long"));
      next.field.focus();
      return;
    }

    this.#busy = true;
    this.#showNote("changing…");
    if (this.#save) this.#save.disabled = true;

    try {
      const answer = await this.#changePassword(current.field.value, next.field.value);
      if (answer === undefined) {
        this.#showNote("Could not reach the server — try again.");
        return;
      }
      if (answer.error !== null) {
        this.#showNote(describeError(answer.error, answer.retryAfterMinutes));
        if (answer.error === "current-wrong") current.field.select();
        return;
      }

      current.clear();
      next.clear();
      this.#showNote("Password changed.");
    } finally {
      this.#busy = false;
      if (this.#save) this.#save.disabled = false;
    }
  }

  #showNote(text: string): void {
    if (this.#note) this.#note.textContent = text;
  }
}
