import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  type ElementRef,
  type OnDestroy,
  type OnInit,
  computed,
  inject,
  signal,
  viewChild,
} from "@angular/core";
import { appUrl } from "../core/app-base";

/**
 * The login screen: one panel, two fields, two modes.
 *
 * Both modes post JSON and act on the answer in place, so a rejected password does not cost
 * a page reload and the typed username survives it. The mode switch is a pair of real
 * buttons rather than a key binding — arrow keys inside a text field belong to the caret,
 * and a login screen is the wrong place to be clever about keyboard capture.
 */

type Mode = "signin" | "signup";

interface AuthResponse {
  readonly succeeded: boolean;
  readonly userName: string | null;
  readonly message: string | null;
  readonly redirectTo: string | null;
}

type MessageKind = "error" | "working" | "none";

@Component({
  selector: "app-login-page",
  templateUrl: "./login-page.html",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LoginPage implements OnInit, OnDestroy {
  readonly #document = inject(DOCUMENT);

  protected readonly mode = signal<Mode>("signin");
  protected readonly caption = computed(() => (this.mode() === "signup" ? "sign up" : "sign in"));
  protected readonly submitting = signal(false);
  protected readonly message = signal("");
  protected readonly messageKind = signal<MessageKind>("none");

  protected readonly userName = viewChild.required<ElementRef<HTMLInputElement>>("userNameInput");
  protected readonly password = viewChild.required<ElementRef<HTMLInputElement>>("passwordInput");
  protected readonly invite = viewChild<ElementRef<HTMLInputElement>>("inviteInput");
  protected readonly signInTab = viewChild.required<ElementRef<HTMLButtonElement>>("signInTab");
  protected readonly signUpTab = viewChild.required<ElementRef<HTMLButtonElement>>("signUpTab");

  ngOnInit(): void {
    this.#document.title = "Novel Reader — sign in";
    this.#document.body.classList.add("login-page");
    queueMicrotask(() => this.userName().nativeElement.focus());
  }

  ngOnDestroy(): void {
    this.#document.body.classList.remove("login-page");
  }

  protected setMode(next: Mode): void {
    this.mode.set(next);
    this.#setMessage(next === "signup" ? "Pick a username, a password of at least 8 characters, and enter your invite code." : "");
  }

  /** Left/right move between the two mode tabs while one of them has focus. */
  protected onTabKey(event: KeyboardEvent): void {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;

    event.preventDefault();
    const next: Mode = event.key === "ArrowRight" ? "signup" : "signin";
    this.setMode(next);
    (next === "signup" ? this.signUpTab() : this.signInTab()).nativeElement.focus();
  }

  protected async submit(event: Event): Promise<void> {
    event.preventDefault();
    if (this.submitting()) return;

    const mode = this.mode();
    const userNameInput = this.userName().nativeElement;
    const passwordInput = this.password().nativeElement;
    const userName = userNameInput.value.trim();
    const password = passwordInput.value;
    const inviteCode = mode === "signup" ? (this.invite()?.nativeElement.value.trim() ?? "") : "";

    if (userName.length === 0 || password.length === 0) {
      this.#setMessage("Both a username and a password are needed.", "error");
      (userName.length === 0 ? userNameInput : passwordInput).focus();
      return;
    }

    if (mode === "signup" && inviteCode.length === 0) {
      this.#setMessage("Signing up needs an invite code.", "error");
      this.invite()?.nativeElement.focus();
      return;
    }

    this.submitting.set(true);
    this.#setMessage(mode === "signup" ? "Creating the account…" : "Signing in…", "working");

    try {
      const response = await fetch(appUrl(`auth/${mode}`), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(mode === "signup" ? { userName, password, inviteCode } : { userName, password }),
        credentials: "same-origin",
      });

      const payload = (await response.json()) as AuthResponse;

      if (response.ok && payload.succeeded) {
        this.#setMessage("Signed in. Opening your novel…");
        // A full load, not a route change: the reading page is the server's to grant.
        window.location.href = appUrl((payload.redirectTo ?? "/ReadingPage").replace(/^\//, ""));
        return;
      }

      this.#setMessage(payload.message ?? "That did not work.", "error");
      passwordInput.select();
    } catch {
      // A network failure is not a rejected password; say so rather than blaming the reader.
      this.#setMessage("Could not reach the server. Is it running?", "error");
    } finally {
      this.submitting.set(false);
    }
  }

  #setMessage(text: string, kind: MessageKind = "none"): void {
    this.message.set(text);
    this.messageKind.set(kind);
  }
}
