/**
 * The comic-panel definition box: a square panel with a triangular tail pointing at the
 * word, paged with j/k, dismissed with Esc.
 *
 * Built with plain DOM calls and no library (D5). Definition text arrives as plain text
 * from the server and is inserted with `textContent`, never `innerHTML`.
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

export class DefinitionBox {
  #root: HTMLElement | undefined;
  #body: HTMLElement | undefined;
  #counter: HTMLElement | undefined;
  #hints: HTMLElement | undefined;
  #view: DefinitionView | undefined;
  #callbacks: DefinitionBoxCallbacks | undefined;
  #senseIndex = 0;
  #isSaved = false;
  /** Kept so the panel can be re-placed when a translation changes its height. */
  #anchor: DOMRect | undefined;
  #translation: TranslationView | undefined;
  #translationPending = false;

  get isOpen(): boolean {
    return this.#root !== undefined;
  }

  get term(): string | undefined {
    return this.#view?.term;
  }

  open(view: DefinitionView, anchor: DOMRect, callbacks: DefinitionBoxCallbacks): void {
    this.close();

    this.#view = view;
    this.#callbacks = callbacks;
    this.#senseIndex = 0;
    this.#isSaved = view.isSaved;
    this.#anchor = anchor;
    this.#translation = undefined;
    this.#translationPending = false;

    const root = document.createElement("div");
    root.className = "definition-box";
    root.setAttribute("role", "dialog");
    root.setAttribute("aria-label", `Definition of ${view.surfaceForm}`);

    const panel = document.createElement("div");
    panel.className = "definition-box__panel";

    panel.appendChild(this.#buildHeader(view));

    const body = document.createElement("div");
    body.className = "definition-box__body";
    panel.appendChild(body);

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

    this.#renderSense();
    this.#renderHints();

    // Height is measured from the first sense and then held, so paging through senses
    // never resizes the panel under the reader (D7).
    this.#lockHeightToCurrentSense();
    this.#position(anchor);
  }

  close(): void {
    this.#root?.remove();
    this.#root = undefined;
    this.#body = undefined;
    this.#counter = undefined;
    this.#hints = undefined;
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
    this.#renderSense();
  }

  previousSense(): void {
    if (!this.#view || this.#view.senses.length === 0) return;
    const count = this.#view.senses.length;
    this.#senseIndex = (this.#senseIndex - 1 + count) % count;
    this.#renderSense();
  }

  /** Reflects a save or delete that has come back from the server. */
  setSaved(isSaved: boolean): void {
    this.#isSaved = isSaved;
    this.#renderHints();
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

  /** The server's answer. Re-lays out the panel so the new block is visible, not scrolled off. */
  showTranslation(translation: TranslationView): void {
    if (!this.#view) return;

    this.#translationPending = false;
    this.#translation = translation;
    this.#relayout();
  }

  #buildHeader(view: DefinitionView): HTMLElement {
    const header = document.createElement("header");
    header.className = "definition-box__header";

    const term = document.createElement("span");
    term.className = "definition-box__term";
    term.textContent = view.surfaceForm;
    header.appendChild(term);

    const counter = document.createElement("span");
    counter.className = "definition-box__counter";
    header.appendChild(counter);
    this.#counter = counter;

    return header;
  }

  #renderSense(): void {
    const body = this.#body;
    const view = this.#view;
    if (!body || !view) return;

    body.replaceChildren();

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
   * Re-measures after the content changed height. The panel is re-placed against the word
   * it was opened on, so a box that grew upwards does not run off the top of the screen.
   */
  #relayout(): void {
    const body = this.#body;
    if (!body) return;

    body.style.height = "";
    this.#renderSense();
    this.#renderHints();
    this.#lockHeightToCurrentSense();
    if (this.#anchor) this.#position(this.#anchor);
  }

  #renderHints(): void {
    const hints = this.#hints;
    const view = this.#view;
    if (!hints || !view) return;

    const parts: string[] = [];
    if (view.senses.length > 1) parts.push("j/k senses");
    parts.push(this.#isSaved ? "d delete" : "s save");
    parts.push(this.#translationPending ? "translating…" : "t translate");
    parts.push("esc close");
    if (view.sourceName) parts.push(`— ${view.sourceName}`);

    hints.textContent = parts.join("  ·  ");
  }

  /**
   * Reads the height the first sense needed and pins it, leaving longer senses to scroll
   * inside the fixed frame.
   */
  #lockHeightToCurrentSense(): void {
    const body = this.#body;
    if (!body) return;

    // Capped so a long sense — or a sense plus a translation — scrolls inside the panel
    // instead of growing it past the viewport.
    const maximum = window.innerHeight * 0.5;
    const measured = Math.min(body.getBoundingClientRect().height, maximum);
    if (measured > 0) body.style.height = `${measured}px`;
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
