import { NgTemplateOutlet } from "@angular/common";
import {
  ChangeDetectionStrategy,
  Component,
  type ElementRef,
  Injector,
  afterNextRender,
  afterRenderEffect,
  computed,
  inject,
  signal,
  viewChild,
} from "@angular/core";
import type { DefinitionView, TranslationLanguageView, TranslationView } from "../core/hub-models";
import { ProgressDots } from "../core/progress-dots";

/**
 * The comic-panel definition box: a square panel with a triangular tail pointing at the
 * word, paged with j/k, dismissed with Esc.
 *
 * Definition text arrives as plain text from the server and is only ever interpolated, never
 * set as HTML (D13).
 *
 * The box opens the moment a lookup is asked for, showing `loading…`, and fills itself in
 * when the answer arrives — so a slow server looks slow rather than looking like nothing
 * happened. Its body is a fixed three lines tall whatever it holds, so that filling in never
 * resizes it under the reader (D27).
 */

/** What the settings form opens with: the stored values, and everything on offer. */
export interface SettingsFormView {
  readonly email: string | null;
  readonly language: string | null;
  readonly languages: readonly TranslationLanguageView[];
}

export interface SettingsFormCallbacks {
  readonly onSubmit: (email: string, language: string) => void;
  readonly onCancel: () => void;
}

export interface DefinitionBoxCallbacks {
  /** Settles when the server has answered, which is when the box stops saying `saving...`. */
  readonly onSave: (term: string) => Promise<void> | void;
  readonly onDelete: (term: string) => Promise<void> | void;
  readonly onTranslate: (term: string) => void;
  readonly onEditSettings: () => void;
  readonly onClose: () => void;
}

/** Distance kept between the box and the word it points at. */
const tailHeight = 12;
const viewportMargin = 12;

/**
 * How long the box waits for an answer before it says so. Long enough that an ordinary
 * dictionary round trip never trips it, short enough that a reader is not left guessing
 * whether the connection is gone (D27).
 */
const lookupTimeoutInMilliseconds = 5000;

/**
 * The same shape the server enforces (D31). This is a courtesy — it catches the typo before a
 * round trip — and never the thing that decides, which is why the server checks it again.
 */
const emailPattern = /^[^@\s]{1,64}@[^@\s.]+(\.[^@\s.]+)+$/;

/** How many languages the picker shows at once before it scrolls. */
export const languageRows = 5;

/**
 * What the body is showing. `slow` and `lost` are both "still waiting", told apart by whether
 * the connection is up — the first is patience, the second is a problem (D33).
 */
type LookupStatus = "loading" | "slow" | "lost" | "ready";

interface ToolbarButton {
  readonly label: string;
  readonly ariaLabel: string;
  readonly run: () => void;
  readonly disabled?: boolean;
  /** Waiting on the server: the label is followed by the progress dots (D48). */
  readonly busy?: boolean;
}

/** One item of the hint bar; `busy` as for a button. */
interface HintPart {
  readonly text: string;
  readonly busy?: boolean;
}

@Component({
  selector: "app-definition-box",
  imports: [NgTemplateOutlet, ProgressDots],
  templateUrl: "./definition-box.html",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DefinitionBox {
  readonly #injector = inject(Injector);

  /** The text that was looked up, known from the moment the box opens. */
  readonly #surfaceForm = signal<string | undefined>(undefined);
  protected readonly status = signal<LookupStatus>("loading");
  protected readonly view = signal<DefinitionView | undefined>(undefined);
  protected readonly senseIndex = signal(0);
  protected readonly isSaved = signal(false);
  /** A save or delete sent and not yet answered (D48); a second press waits for it. */
  protected readonly change = signal<"saving" | "deleting" | undefined>(undefined);
  #pendingChange: Promise<void> | undefined;
  /** Kept so the panel can be re-placed when its content changes. */
  readonly #anchor = signal<DOMRect | undefined>(undefined);
  protected readonly translation = signal<TranslationView | undefined>(undefined);
  protected readonly translationPending = signal(false);
  /**
   * Which of the two things the panel is: the definition it was opened for, or the form that
   * asks how translation should work. The form is a detour — cancelling it returns to the
   * definition rather than closing the box (D31).
   */
  protected readonly mode = signal<"lookup" | "settings">("lookup");
  protected readonly settings = signal<SettingsFormView | undefined>(undefined);
  protected readonly languageFilter = signal("");
  protected readonly formMessage = signal("");
  /** Whether the hub is reachable, as the page last reported it (D28). */
  readonly #connectionUp = signal(true);

  readonly #callbacks = signal<DefinitionBoxCallbacks | undefined>(undefined);
  #settingsCallbacks: SettingsFormCallbacks | undefined;
  #timeoutTimer: number | undefined;
  /** Set when the translation block should be scrolled into the three lines on show. */
  #revealTranslation = false;
  /** Set when a sense has just been drawn, which starts at the top of the body. */
  #scrollBodyToTop = false;

  protected readonly languageRows = languageRows;

  protected readonly root = viewChild<ElementRef<HTMLElement>>("root");
  protected readonly body = viewChild<ElementRef<HTMLElement>>("body");
  protected readonly emailField = viewChild<ElementRef<HTMLInputElement>>("email");
  protected readonly filterField = viewChild<ElementRef<HTMLInputElement>>("filter");
  protected readonly languageList = viewChild<ElementRef<HTMLSelectElement>>("list");

  readonly isOpen = computed(() => this.#surfaceForm() !== undefined);

  /** What was asked for, which is what an incoming answer is matched against. */
  get surfaceForm(): string | undefined {
    return this.#surfaceForm();
  }

  protected readonly heading = computed(() => this.#surfaceForm() ?? "");

  /** The normalised term, once the server has answered. */
  get term(): string | undefined {
    return this.view()?.term;
  }

  get isEditingSettings(): boolean {
    return this.mode() === "settings";
  }

  protected readonly sense = computed(() => this.view()?.senses[this.senseIndex()]);

  protected readonly counter = computed(() => {
    const view = this.view();
    if (this.mode() === "settings" || this.status() !== "ready" || !view?.found || view.senses.length === 0) return "";
    return `${this.senseIndex() + 1}/${view.senses.length}`;
  });

  /** Waiting is just waiting; red is reserved for the case that is genuinely wrong (D33). */
  protected readonly waitingMessage = computed(() => {
    switch (this.status()) {
      case "lost":
        return "connection lost";
      case "slow":
        return "still looking…";
      default:
        return "loading…";
    }
  });

  protected readonly languageOptions = computed(() => {
    const settings = this.settings();
    if (!settings) return [];

    const needle = this.languageFilter().trim().toLowerCase();
    return settings.languages.filter((language) =>
      needle.length === 0
      || language.name.toLowerCase().includes(needle)
      || language.code.toLowerCase().startsWith(needle));
  });

  /** The stored language when the filter keeps it, otherwise the first row. */
  protected readonly preferredLanguage = computed(() => {
    const options = this.languageOptions();
    const stored = this.settings()?.language;
    return options.find((language) => language.code === stored)?.code ?? options[0]?.code;
  });

  protected readonly hints = computed<readonly HintPart[]>(() => {
    if (this.mode() === "settings") return [{ text: "enter next" }, { text: "esc cancel" }];

    const view = this.view();
    // Nothing but Escape means anything until there is a definition to act on — except the
    // translation, which may already be here or on its way (D32).
    if (!view) return this.translationPending() ? [{ text: "translating…" }, { text: "esc close" }] : [{ text: "esc close" }];

    const change = this.change();
    const parts: HintPart[] = [];
    if (view.senses.length > 1) parts.push({ text: "j/k senses" });
    parts.push(change ? { text: change, busy: true } : { text: this.isSaved() ? "d delete" : "s save" });
    parts.push({ text: this.translationPending() ? "translating…" : "t translate" });
    parts.push({ text: "e settings" });
    parts.push({ text: "esc close" });
    if (view.sourceName) parts.push({ text: `— ${view.sourceName}` });
    return parts;
  });

  /** The tap toolbar: the same actions as the key hints, one button each. */
  protected readonly buttons = computed<readonly ToolbarButton[]>(() => {
    if (this.mode() === "settings") {
      return [
        { label: "save", ariaLabel: "Save translation settings", run: () => this.#submitSettings() },
        { label: "cancel", ariaLabel: "Cancel", run: () => this.#settingsCallbacks?.onCancel() },
      ];
    }

    const view = this.view();
    if (!view) return [{ label: "close", ariaLabel: "Close", run: () => this.requestClose() }];

    const buttons: ToolbarButton[] = [];
    if (view.senses.length > 1) {
      buttons.push({ label: "‹", ariaLabel: "Previous sense", run: () => this.previousSense() });
      buttons.push({ label: "›", ariaLabel: "Next sense", run: () => this.nextSense() });
    }

    const change = this.change();
    buttons.push(change
      ? { label: change, ariaLabel: change === "saving" ? "Saving word" : "Deleting word", run: () => {}, disabled: true, busy: true }
      : this.isSaved()
        ? { label: "delete", ariaLabel: "Delete word", run: () => this.deleteTerm() }
        : { label: "save", ariaLabel: "Save word", run: () => this.save() });

    const pending = this.translationPending();
    buttons.push({ label: pending ? "translating…" : "translate", ariaLabel: "Translate", run: () => this.translate(), disabled: pending });

    // The phone's `e`: without it a touch reader could set translation up once and never
    // change it (D31).
    buttons.push({ label: "settings", ariaLabel: "Translation settings", run: () => this.editSettings() });
    buttons.push({ label: "close", ariaLabel: "Close", run: () => this.requestClose() });
    return buttons;
  });

  constructor() {
    // Re-placed after every render that could change its size: the body is a fixed height
    // (D27), so this only moves the panel when the hint bar or the toolbar wraps.
    afterRenderEffect(() => {
      this.hints();
      this.buttons();
      this.mode();
      this.status();
      this.senseIndex();
      this.translation();
      this.translationPending();
      const anchor = this.#anchor();
      const root = this.root()?.nativeElement;
      if (!anchor || !root) return;

      this.#position(root, anchor);

      const body = this.body()?.nativeElement;
      if (this.#scrollBodyToTop && body) {
        this.#scrollBodyToTop = false;
        body.scrollTop = 0;
      }

      // The sense is rendered above the translation, so a translation the reader is reading
      // would be pushed out of the three-line frame by the definition arriving.
      if (this.#revealTranslation) {
        this.#revealTranslation = false;
        body?.querySelector(".definition-box__translation")?.scrollIntoView({ block: "nearest" });
      }
    });
  }

  /**
   * Opens the box on a lookup that is still in flight. The answer arrives at
   * {@link showDefinition}; if none does, the box says so after
   * {@link lookupTimeoutInMilliseconds}.
   */
  open(surfaceForm: string, anchor: DOMRect, callbacks: DefinitionBoxCallbacks): void {
    this.close();

    this.#surfaceForm.set(surfaceForm);
    this.#anchor.set(anchor);
    this.#callbacks.set(callbacks);

    this.#timeoutTimer = window.setTimeout(() => {
      this.#timeoutTimer = undefined;
      if (this.status() !== "loading") return;

      // Slow, or actually broken — the connection state is what tells them apart.
      this.status.set(this.#connectionUp() ? "slow" : "lost");
    }, lookupTimeoutInMilliseconds);
  }

  /**
   * The server's answer. Accepted even after the box has given up waiting — a late answer is
   * still the answer the reader asked for, and it replaces the timeout message.
   */
  showDefinition(view: DefinitionView): void {
    if (!this.isOpen()) return;

    this.#clearTimeout();
    this.status.set("ready");
    this.view.set(view);
    this.senseIndex.set(0);
    this.isSaved.set(view.isSaved);
    this.#scrollBodyToTop = true;
    if (this.translation() || this.translationPending()) this.#revealTranslation = true;
  }

  /**
   * Turns the panel into the translation settings form. Used both when the reader has no
   * settings yet and when they press `e` to change them, which is why it takes what is stored
   * rather than assuming it is empty.
   */
  openSettings(settings: SettingsFormView, callbacks: SettingsFormCallbacks): void {
    if (!this.isOpen()) return;

    this.mode.set("settings");
    this.settings.set(settings);
    this.languageFilter.set("");
    this.formMessage.set("");
    this.#settingsCallbacks = callbacks;

    // The email is the field the reader has to think about; the language has a sensible
    // default sitting in the list below it.
    afterNextRender(() => {
      const email = this.emailField()?.nativeElement;
      email?.focus();
      email?.select();
    }, { injector: this.#injector });
  }

  /** Leaves the form without saving, putting the definition back. */
  cancelSettings(): void {
    if (this.mode() !== "settings") return;

    this.mode.set("lookup");
    this.#settingsCallbacks = undefined;
  }

  close(): void {
    this.#clearTimeout();
    this.#surfaceForm.set(undefined);
    this.status.set("loading");
    this.view.set(undefined);
    this.#callbacks.set(undefined);
    this.senseIndex.set(0);
    this.isSaved.set(false);
    this.change.set(undefined);
    this.#pendingChange = undefined;
    this.#anchor.set(undefined);
    this.translation.set(undefined);
    this.translationPending.set(false);
    this.mode.set("lookup");
    this.settings.set(undefined);
    this.#settingsCallbacks = undefined;
    this.formMessage.set("");
  }

  nextSense(): void {
    const count = this.view()?.senses.length ?? 0;
    if (count === 0) return;
    this.senseIndex.update((index) => (index + 1) % count);
    this.#scrollBodyToTop = true;
  }

  previousSense(): void {
    const count = this.view()?.senses.length ?? 0;
    if (count === 0) return;
    this.senseIndex.update((index) => (index - 1 + count) % count);
    this.#scrollBodyToTop = true;
  }

  /**
   * Told by the page whenever the link to the server changes (D28). A definition that is
   * merely slow becomes a real problem the moment the connection goes, and stops being one
   * when it comes back — so the message follows the truth rather than a timer.
   */
  setConnectionUp(up: boolean): void {
    this.#connectionUp.set(up);

    if (this.status() === "slow" && !up) this.status.set("lost");
    else if (this.status() === "lost" && up) this.status.set("slow");
  }

  /** Reflects a save or delete that has come back from the server. */
  setSaved(isSaved: boolean): void {
    this.isSaved.set(isSaved);
  }

  save(): void {
    const view = this.view();
    const callbacks = this.#callbacks();
    if (view && callbacks && !this.change()) this.#track("saving", callbacks.onSave(view.term));
  }

  deleteTerm(): void {
    const view = this.view();
    const callbacks = this.#callbacks();
    if (view && callbacks && !this.change()) this.#track("deleting", callbacks.onDelete(view.term));
  }

  /**
   * Shows `saving...` until the hub call settles. The vocabulary push that flips save to
   * delete arrives before the call's own answer, so the dots give way to the new state, never
   * to the old one. A call that fails clears them too, and the button is pressable again.
   */
  #track(change: "saving" | "deleting", result: Promise<void> | void): void {
    const pending = Promise.resolve(result);
    this.#pendingChange = pending;
    this.change.set(change);

    void pending.finally(() => {
      // A box closed or reopened on another word since then has its own state.
      if (this.#pendingChange !== pending) return;
      this.#pendingChange = undefined;
      this.change.set(undefined);
    });
  }

  translate(): void {
    if (this.mode() === "settings") return;
    // The surface form, not the definition's term: a translation can be asked for before the
    // definition has arrived, and there is no term yet to ask with (D32).
    const surfaceForm = this.#surfaceForm();
    if (surfaceForm) this.#callbacks()?.onTranslate(surfaceForm);
  }

  /** The `e` key, and the toolbar's settings button. */
  editSettings(): void {
    this.#callbacks()?.onEditSettings();
  }

  requestClose(): void {
    this.#callbacks()?.onClose();
  }

  /** Shown between asking for a translation and the server answering. */
  showTranslationPending(): void {
    if (!this.isOpen()) return;

    this.translationPending.set(true);
    this.translation.set(undefined);
    this.#revealTranslation = true;
  }

  /** The server's answer, shown under the sense. */
  showTranslation(translation: TranslationView): void {
    if (!this.isOpen()) return;

    this.translationPending.set(false);
    this.translation.set(translation);
    this.#revealTranslation = true;
  }

  /** Reports a refusal that came back from the server, and puts the reader on the bad field. */
  showSettingsError(message: string, field: "email" | "language"): void {
    this.formMessage.set(message);
    afterNextRender(() => {
      (field === "email" ? this.emailField() : this.filterField())?.nativeElement.focus();
    }, { injector: this.#injector });
  }

  // ---- The settings form's keys: type to narrow, Enter forward, Escape back (D31) ----

  protected onEmailKey(event: KeyboardEvent): void {
    if (event.key === "Escape") {
      event.preventDefault();
      this.#settingsCallbacks?.onCancel();
      return;
    }

    if (event.key !== "Enter") return;
    event.preventDefault();

    if (!this.#validateEmail()) return;

    this.formMessage.set("");
    this.filterField()?.nativeElement.focus();
  }

  protected onFilterKey(event: KeyboardEvent): void {
    if (event.key === "Escape") {
      event.preventDefault();
      this.#settingsCallbacks?.onCancel();
      return;
    }

    // Enter and Down both hand over to the list, which is where the choosing happens.
    if (event.key !== "Enter" && event.key !== "ArrowDown") return;
    event.preventDefault();

    const list = this.languageList()?.nativeElement;
    if (!list || list.options.length === 0) {
      this.formMessage.set("No language matches that.");
      return;
    }

    if (list.selectedIndex < 0) list.selectedIndex = 0;
    list.focus();
  }

  /**
   * Deliberately no click-to-submit on the list. Tapping a <select> on Android opens the
   * browser's own picker, so a click handler here fired at the same time and left that picker
   * stranded over the page. Touch picks from the native list and taps `save`; a keyboard
   * presses Enter.
   */
  protected onListKey(event: KeyboardEvent): void {
    if (event.key === "Escape") {
      event.preventDefault();
      this.#settingsCallbacks?.onCancel();
      return;
    }

    if (event.key !== "Enter") return;
    event.preventDefault();
    this.#submitSettings();
  }

  #validateEmail(): boolean {
    const field = this.emailField()?.nativeElement;
    const value = (field?.value ?? "").trim();

    if (!emailPattern.test(value)) {
      this.formMessage.set("That does not look like an email address.");
      field?.focus();
      return false;
    }

    return true;
  }

  #submitSettings(): void {
    if (!this.#validateEmail()) return;

    const language = this.languageList()?.nativeElement.value ?? "";
    if (language.length === 0) {
      this.formMessage.set("Pick a language from the list.");
      this.filterField()?.nativeElement.focus();
      return;
    }

    const email = (this.emailField()?.nativeElement.value ?? "").trim();
    this.#settingsCallbacks?.onSubmit(email, language);
  }

  #clearTimeout(): void {
    if (this.#timeoutTimer === undefined) return;

    clearTimeout(this.#timeoutTimer);
    this.#timeoutTimer = undefined;
  }

  /**
   * Places the panel under the word, flipping above it when there is not enough room, and
   * keeps the tail pointing at the word even when the panel is nudged off-centre to stay
   * on screen.
   */
  #position(root: HTMLElement, anchor: DOMRect): void {
    // The panel's width is settled in CSS, so this measures what will actually be drawn
    // wherever the panel ends up — it does not depend on where it currently sits.
    const panelWidth = root.offsetWidth;
    const panelHeight = root.offsetHeight;

    // `clientWidth`, not `innerWidth`: the latter counts the scrollbar's gutter, and a panel
    // pushed against the right edge would be placed partly underneath it.
    const viewportWidth = document.documentElement.clientWidth;

    const spaceBelow = window.innerHeight - anchor.bottom;
    const placeBelow = spaceBelow >= panelHeight + tailHeight + viewportMargin
      || spaceBelow >= anchor.top;

    root.classList.toggle("definition-box--below", placeBelow);
    root.classList.toggle("definition-box--above", !placeBelow);

    const top = placeBelow
      ? anchor.bottom + tailHeight
      : anchor.top - panelHeight - tailHeight;

    const anchorCentre = anchor.left + anchor.width / 2;
    const maxLeft = viewportWidth - panelWidth - viewportMargin;
    const left = Math.max(viewportMargin, Math.min(anchorCentre - panelWidth / 2, maxLeft));

    // Through the CSSOM, which the CSP allows; a style attribute it would not (D42).
    root.style.top = `${Math.max(viewportMargin, top)}px`;
    root.style.left = `${left}px`;

    // Tail sits over the word rather than the middle of a shifted panel.
    const tailOffset = Math.max(16, Math.min(anchorCentre - left, panelWidth - 16));
    root.style.setProperty("--tail-offset", `${tailOffset}px`);
  }
}
