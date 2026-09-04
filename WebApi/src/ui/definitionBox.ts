/**
 * The comic-panel definition box: a square panel with a triangular tail pointing at the
 * word, paged with j/k, dismissed with Esc.
 *
 * Built with plain DOM calls and no library (D5). Definition text arrives as plain text
 * from the server and is inserted with `textContent`, never `innerHTML`.
 *
 * The box opens the moment a lookup is asked for, showing `loading…`, and fills itself in
 * when the answer arrives — so a slow server looks slow rather than looking like nothing
 * happened. Its body is a fixed three lines tall whatever it holds, so that filling in never
 * resizes it under the reader (D27).
 */

export interface DefinitionSenseView {
  readonly partOfSpeech: string | null;
  readonly text: string;
  readonly example: string | null;
}

export interface DefinitionView {
  readonly term: string;
  readonly surfaceForm: string;
  readonly senses: readonly DefinitionSenseView[];
  readonly sourceName: string | null;
  readonly isSaved: boolean;
  readonly found: boolean;
}

/** A translation as the server answered it; `note` carries provenance for the hint line. */
export interface TranslationView {
  readonly text: string;
  readonly note: string | null;
}

export interface DefinitionBoxCallbacks {
  readonly onSave: (term: string) => void;
  readonly onDelete: (term: string) => void;
  readonly onTranslate: (term: string) => void;
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

/** Shown when nothing has come back within that window. */
const timeoutMessage = "connection timeout";

/** What the body is showing: still waiting, given up waiting, or the definition itself. */
type LookupStatus = "loading" | "timedOut" | "ready";

export class DefinitionBox {
  #root: HTMLElement | undefined;
  #body: HTMLElement | undefined;
  #counter: HTMLElement | undefined;
  #hints: HTMLElement | undefined;
  /** Tap equivalents of the key hints, shown on touch (CSS). Mirror `#renderHints`. */
  #buttons: HTMLElement | undefined;
  /** The text that was looked up, known from the moment the box opens. */
  #surfaceForm: string | undefined;
  #status: LookupStatus = "loading";
  #timeoutTimer: number | undefined;
  #view: DefinitionView | undefined;
  #callbacks: DefinitionBoxCallbacks | undefined;
  #senseIndex = 0;
  #isSaved = false;
  /** Kept so the panel can be re-placed when its content changes. */
  #anchor: DOMRect | undefined;
  #translation: TranslationView | undefined;
  #translationPending = false;

  get isOpen(): boolean {
    return this.#root !== undefined;
  }

  /** The normalised term, once the server has answered. */
  get term(): string | undefined {
    return this.#view?.term;
  }

  /** What was asked for, which is what an incoming answer is matched against. */
  get surfaceForm(): string | undefined {
    return this.#surfaceForm;
  }

  /**
   * Opens the box on a lookup that is still in flight. The answer arrives at
   * {@link showDefinition}; if none does, the box says so after
   * {@link lookupTimeoutInMilliseconds}.
   */
  open(surfaceForm: string, anchor: DOMRect, callbacks: DefinitionBoxCallbacks): void {
    this.close();

    this.#surfaceForm = surfaceForm;
    this.#status = "loading";
    this.#view = undefined;
    this.#callbacks = callbacks;
    this.#senseIndex = 0;
    this.#isSaved = false;
    this.#anchor = anchor;
    this.#translation = undefined;
    this.#translationPending = false;

    const root = document.createElement("div");
    root.className = "definition-box";
    root.setAttribute("role", "dialog");
    root.setAttribute("aria-label", `Definition of ${surfaceForm}`);

    const panel = document.createElement("div");
    panel.className = "definition-box__panel";

    panel.appendChild(this.#buildHeader(surfaceForm));

    const body = document.createElement("div");
    body.className = "definition-box__body";
    panel.appendChild(body);

    // The tap toolbar sits above the key-hint bar; CSS shows exactly one of the two,
    // buttons on touch and the hint text on a keyboard.
    const buttons = document.createElement("div");
    buttons.className = "definition-box__buttons";
    panel.appendChild(buttons);

    const hints = document.createElement("footer");
    hints.className = "definition-box__hints";
    panel.appendChild(hints);

    const tail = document.createElement("div");
    tail.className = "definition-box__tail";

    root.appendChild(panel);
    root.appendChild(tail);
    document.body.appendChild(root);

    this.#root = root;
    this.#body = body;
    this.#hints = hints;
    this.#buttons = buttons;

    this.#renderBody();
    this.#renderHints();
    this.#renderButtons();
    this.#position(anchor);

    this.#timeoutTimer = window.setTimeout(() => {
      this.#timeoutTimer = undefined;
      if (this.#status !== "loading") return;

      this.#status = "timedOut";
      this.#relayout();
    }, lookupTimeoutInMilliseconds);
  }

  /**
   * The server's answer. Accepted even after the box has given up waiting — a late answer is
   * still the answer the reader asked for, and it replaces the timeout message.
   */
  showDefinition(view: DefinitionView): void {
    if (!this.#root) return;

    this.#clearTimeout();
    this.#status = "ready";
    this.#view = view;
    this.#senseIndex = 0;
    this.#isSaved = view.isSaved;
    this.#relayout();
  }

  close(): void {
    this.#clearTimeout();
    this.#root?.remove();
    this.#root = undefined;
    this.#body = undefined;
    this.#counter = undefined;
    this.#hints = undefined;
    this.#buttons = undefined;
    this.#surfaceForm = undefined;
    this.#status = "loading";
    this.#view = undefined;
    this.#callbacks = undefined;
    this.#senseIndex = 0;
    this.#anchor = undefined;
    this.#translation = undefined;
    this.#translationPending = false;
  }

  nextSense(): void {
    if (!this.#view || this.#view.senses.length === 0) return;
    this.#senseIndex = (this.#senseIndex + 1) % this.#view.senses.length;
    this.#renderBody();
  }

  previousSense(): void {
    if (!this.#view || this.#view.senses.length === 0) return;
    const count = this.#view.senses.length;
    this.#senseIndex = (this.#senseIndex - 1 + count) % count;
    this.#renderBody();
  }

  /** Reflects a save or delete that has come back from the server. */
  setSaved(isSaved: boolean): void {
    this.#isSaved = isSaved;
    this.#renderHints();
    this.#renderButtons();
  }

  save(): void {
    if (this.#view) this.#callbacks?.onSave(this.#view.term);
  }

  deleteTerm(): void {
    if (this.#view) this.#callbacks?.onDelete(this.#view.term);
  }

  translate(): void {
    if (this.#view) this.#callbacks?.onTranslate(this.#view.term);
  }

  requestClose(): void {
    this.#callbacks?.onClose();
  }

  /** Shown between pressing `t` and the server answering. */
  showTranslationPending(): void {
    if (!this.#view) return;

    this.#translationPending = true;
    this.#translation = undefined;
    this.#relayout();
  }

  /** The server's answer. Re-renders so the new block is in place under the sense. */
  showTranslation(translation: TranslationView): void {
    if (!this.#view) return;

    this.#translationPending = false;
    this.#translation = translation;
    this.#relayout();
  }

  #clearTimeout(): void {
    if (this.#timeoutTimer === undefined) return;

    clearTimeout(this.#timeoutTimer);
    this.#timeoutTimer = undefined;
  }

  #buildHeader(surfaceForm: string): HTMLElement {
    const header = document.createElement("header");
    header.className = "definition-box__header";

    const term = document.createElement("span");
    term.className = "definition-box__term";
    term.textContent = surfaceForm;
    header.appendChild(term);

    const counter = document.createElement("span");
    counter.className = "definition-box__counter";
    header.appendChild(counter);
    this.#counter = counter;

    return header;
  }

  /** The body, in whichever of the three states the lookup is in. */
  #renderBody(): void {
    const body = this.#body;
    if (!body) return;

    body.replaceChildren();

    if (this.#status !== "ready" || !this.#view) {
      const waiting = document.createElement("p");
      waiting.className = this.#status === "timedOut"
        ? "definition-box__error"
        : "definition-box__loading";
      waiting.textContent = this.#status === "timedOut" ? timeoutMessage : "loading…";
      body.appendChild(waiting);

      if (this.#counter) this.#counter.textContent = "";
      return;
    }

    this.#renderSense(body, this.#view);
  }

  #renderSense(body: HTMLElement, view: DefinitionView): void {
    if (!view.found || view.senses.length === 0) {
      const empty = document.createElement("p");
      empty.className = "definition-box__empty";
      empty.textContent = "No definition found.";
      body.appendChild(empty);
      if (this.#counter) this.#counter.textContent = "";
      // A word with no dictionary entry is exactly when a translation is worth seeing.
      this.#appendTranslation(body);
      return;
    }

    const sense = view.senses[this.#senseIndex];
    if (!sense) return;

    if (sense.partOfSpeech) {
      const partOfSpeech = document.createElement("span");
      partOfSpeech.className = "definition-box__pos";
      partOfSpeech.textContent = sense.partOfSpeech.toLowerCase();
      body.appendChild(partOfSpeech);
    }

    const text = document.createElement("p");
    text.className = "definition-box__text";
    text.textContent = sense.text;
    body.appendChild(text);

    if (sense.example) {
      const example = document.createElement("p");
      example.className = "definition-box__example";
      example.textContent = sense.example;
      body.appendChild(example);
    }

    if (this.#counter) {
      this.#counter.textContent = `${this.#senseIndex + 1}/${view.senses.length}`;
    }

    this.#appendTranslation(body);

    body.scrollTop = 0;
  }

  /** The translation block, re-added after every sense render so paging never drops it. */
  #appendTranslation(body: HTMLElement): void {
    if (!this.#translationPending && !this.#translation) return;

    const block = document.createElement("div");
    block.className = "definition-box__translation";
    if (this.#translationPending) block.classList.add("definition-box__translation--pending");

    const label = document.createElement("span");
    label.className = "definition-box__translation-label";
    label.textContent = "translation";
    block.appendChild(label);

    const text = document.createElement("p");
    text.className = "definition-box__translation-text";
    text.textContent = this.#translationPending
      ? "translating…"
      : (this.#translation?.text ?? "");
    block.appendChild(text);

    const note = this.#translation?.note;
    if (note) {
      const noteElement = document.createElement("span");
      noteElement.className = "definition-box__translation-note";
      noteElement.textContent = note;
      block.appendChild(noteElement);
    }

    body.appendChild(block);
  }

  /**
   * Redraws everything the state feeds and re-places the panel. The body's height is fixed
   * in CSS (D27), so this only ever moves the panel when the hint bar or the toolbar wraps
   * onto another line.
   */
  #relayout(): void {
    if (!this.#root) return;

    this.#renderBody();
    this.#renderHints();
    this.#renderButtons();
    if (this.#anchor) this.#position(this.#anchor);
  }

  #renderHints(): void {
    const hints = this.#hints;
    if (!hints) return;

    const view = this.#view;
    const parts: string[] = [];

    // Nothing but Escape means anything until there is a definition to act on.
    if (!view) {
      hints.textContent = "esc close";
      return;
    }

    if (view.senses.length > 1) parts.push("j/k senses");
    parts.push(this.#isSaved ? "d delete" : "s save");
    parts.push(this.#translationPending ? "translating…" : "t translate");
    parts.push("esc close");
    if (view.sourceName) parts.push(`— ${view.sourceName}`);

    hints.textContent = parts.join("  ·  ");
  }

  /**
   * The tap toolbar: the same actions as the key hints, one button each. Rebuilt whenever
   * the state behind a hint changes — the sense count, the saved flag, a translation in
   * flight — so it never falls out of step with `#renderHints`.
   */
  #renderButtons(): void {
    const container = this.#buttons;
    if (!container) return;

    container.replaceChildren();

    const add = (label: string, ariaLabel: string, run: () => void, disabled = false): void => {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "definition-box__button";
      button.textContent = label;
      button.setAttribute("aria-label", ariaLabel);
      button.disabled = disabled;
      button.addEventListener("click", run);
      container.appendChild(button);
    };

    const view = this.#view;
    if (!view) {
      add("close", "Close", () => this.requestClose());
      return;
    }

    if (view.senses.length > 1) {
      add("‹", "Previous sense", () => this.previousSense());
      add("›", "Next sense", () => this.nextSense());
    }

    if (this.#isSaved) {
      add("delete", "Delete word", () => this.deleteTerm());
    } else {
      add("save", "Save word", () => this.save());
    }

    add(
      this.#translationPending ? "translating…" : "translate",
      "Translate",
      () => this.translate(),
      this.#translationPending,
    );

    add("close", "Close", () => this.requestClose());
  }

  /**
   * Places the panel under the word, flipping above it when there is not enough room, and
   * keeps the tail pointing at the word even when the panel is nudged off-centre to stay
   * on screen.
   */
  #position(anchor: DOMRect): void {
    const root = this.#root;
    if (!root) return;

    const panelWidth = root.offsetWidth;
    const panelHeight = root.offsetHeight;

    const spaceBelow = window.innerHeight - anchor.bottom;
    const placeBelow = spaceBelow >= panelHeight + tailHeight + viewportMargin
      || spaceBelow >= anchor.top;

    root.classList.toggle("definition-box--below", placeBelow);
    root.classList.toggle("definition-box--above", !placeBelow);

    const top = placeBelow
      ? anchor.bottom + tailHeight
      : anchor.top - panelHeight - tailHeight;

    const anchorCentre = anchor.left + anchor.width / 2;
    const maxLeft = window.innerWidth - panelWidth - viewportMargin;
    const left = Math.max(viewportMargin, Math.min(anchorCentre - panelWidth / 2, maxLeft));

    root.style.top = `${Math.max(viewportMargin, top)}px`;
    root.style.left = `${left}px`;

    // Tail sits over the word rather than the middle of a shifted panel.
    const tailOffset = Math.max(16, Math.min(anchorCentre - left, panelWidth - 16));
    root.style.setProperty("--tail-offset", `${tailOffset}px`);
  }
}
