import { NgTemplateOutlet } from "@angular/common";
import { ChangeDetectionStrategy, Component, type ElementRef, afterRenderEffect, inject, signal, untracked, viewChild } from "@angular/core";
import { ReaderConnection } from "../core/reader-connection";
import { SettingsPanel } from "./settings-panel";

/** The server's limits; checked here only to save a round trip. */
const minimumPasswordLength = 8;
const maximumPasswordLength = 128;

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

/**
 * Changes the signed-in reader's password (D40). The current password is required, so a
 * session left open on someone else's screen cannot be turned into the account. The new one
 * is typed once: instead of a second field to repeat it in, the eye beside it shows what was
 * typed. Both fields are cleared and hidden again every time the drawer opens, so a revealed
 * password is not left on screen.
 */
@Component({
  selector: "app-password-section",
  imports: [NgTemplateOutlet],
  templateUrl: "./password-section.html",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PasswordSection {
  readonly #connection = inject(ReaderConnection);

  protected readonly minimumPasswordLength = minimumPasswordLength;
  protected readonly revealCurrent = signal(false);
  protected readonly revealNext = signal(false);
  protected readonly note = signal("");
  protected readonly busy = signal(false);

  protected readonly current = viewChild.required<ElementRef<HTMLInputElement>>("current");
  protected readonly next = viewChild.required<ElementRef<HTMLInputElement>>("next");

  constructor() {
    const panel = inject(SettingsPanel);
    afterRenderEffect(() => {
      panel.openings();
      untracked(() => {
        this.#clear();
        this.note.set("");
      });
    });
  }

  protected async submit(): Promise<void> {
    const current = this.current().nativeElement;
    const next = this.next().nativeElement;
    if (this.busy()) return;

    if (current.value.length === 0) {
      this.note.set("Enter your current password.");
      current.focus();
      return;
    }
    if (next.value.length < minimumPasswordLength || next.value.length > maximumPasswordLength) {
      this.note.set(describeError(next.value.length < minimumPasswordLength ? "too-short" : "too-long"));
      next.focus();
      return;
    }

    this.busy.set(true);
    this.note.set("changing…");

    try {
      const answer = await this.#connection.changePassword(current.value, next.value);
      if (answer === undefined) {
        this.note.set("Could not reach the server — try again.");
        return;
      }
      if (answer.error !== null) {
        this.note.set(describeError(answer.error, answer.retryAfterMinutes));
        if (answer.error === "current-wrong") current.select();
        return;
      }

      this.#clear();
      this.note.set("Password changed.");
    } finally {
      this.busy.set(false);
    }
  }

  /** Empty and hidden, as it is whenever the drawer opens. */
  #clear(): void {
    this.current().nativeElement.value = "";
    this.next().nativeElement.value = "";
    this.revealCurrent.set(false);
    this.revealNext.set(false);
  }
}
