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

/**
 * A translation as the server answered it. Exactly one of `text` and `error` is set; `note`
 * carries the language it came back in.
 */
export interface TranslationView {
  readonly text: string | null;
  readonly note: string | null;
  readonly error: string | null;
}

export interface TranslationLanguageOption {
  readonly code: string;
  readonly name: string;
}

/** What the settings form opens with: the stored values, and everything on offer. */
export interface SettingsFormView {
  readonly email: string | null;
  readonly language: string | null;
  readonly languages: readonly TranslationLanguageOption[];
}

export interface SettingsFormCallbacks {
  readonly onSubmit: (email: string, language: string) => void;
  readonly onCancel: () => void;
}

export interface DefinitionBoxCallbacks {
  readonly onSave: (term: string) => void;
  readonly onDelete: (term: string) => void;
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
 * What the box says after that, and it depends on which of the two is actually true. A slow
 * dictionary is not a broken connection: a phrase misses Wiktionary and falls through to the
 * second provider, which routinely takes seven seconds (D1, D32), and calling that a
 * connection timeout in red is both untrue and alarming.
 */
const slowMessage = "still looking…";
const lostMessage = "connection lost";

/**
 * The same shape the server enforces (D31). This is a courtesy — it catches the typo before a
 * round trip — and never the thing that decides, which is why the server checks it again.
 */
const emailPattern = /^[^@\s]{1,64}@[^@\s.]+(\.[^@\s.]+)+$/;

/** How many languages the picker shows at once before it scrolls. */
const languageRows = 5;

/**
 * What the body is showing. `slow` and `lost` are both "still waiting", told apart by whether
 * the connection is up — the first is patience, the second is a problem (D33).
 */
type LookupStatus = "loading" | "slow" | "lost" | "ready";

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
  /**
   * Which of the two things the panel is: the definition it was opened for, or the form that
   * asks how translation should work. The form is a detour — cancelling it returns to the
   * definition rather than closing the box (D31).
   */
  #mode: "lookup" | "settings" = "lookup";
  #settings: SettingsFormView | undefined;
  #settingsCallbacks: SettingsFormCallbacks | undefined;
  /** Whether the hub is reachable, as the page last reported it (D28). */
  #connectionUp = true;
  #emailField: HTMLInputElement | undefined;
  #languageFilter: HTMLInputElement | undefined;
  #languageList: HTMLSelectElement | undefined;
  #formMessage: HTMLElement | undefined;

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

      // Slow, or actually broken — the connection state is what tells them apart.
      this.#status = this.#connectionUp ? "slow" : "lost";
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

    // The sense is rendered above the translation and resets the scroll, so a translation the
    // reader is already reading would be pushed out of the frame by the definition arriving.
    if (this.#translation || this.#translationPending) this.#revealTranslation();
  }

  /**
   * Turns the panel into the translation settings form. Used both when the reader has no
   * settings yet and when they press `e` to change them, which is why it takes what is stored
   * rather than assuming it is empty.
   */
  openSettings(settings: SettingsFormView, callbacks: SettingsFormCallbacks): void {
    if (!this.#root) return;

    this.#mode = "settings";
    this.#settings = settings;
    this.#settingsCallbacks = callbacks;
    this.#relayout();

    // The email is the field the reader has to think about; the language has a sensible
    // default sitting in the list below it.
    this.#emailField?.focus();
    this.#emailField?.select();
  }

  /** Leaves the form without saving, putting the definition back. */
  cancelSettings(): void {
    if (this.#mode !== "settings") return;

    this.#mode = "lookup";
    this.#settingsCallbacks = undefined;
    this.#relayout();
  }

  get isEditingSettings(): boolean {
    return this.#mode === "settings";
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
    this.#mode = "lookup";
    this.#settings = undefined;
    this.#settingsCallbacks = undefined;
    this.#emailField = undefined;
    this.#languageFilter = undefined;
    this.#languageList = undefined;
    this.#formMessage = undefined;
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

  /**
   * Told by the page whenever the link to the server changes (D28). A definition that is
   * merely slow becomes a real problem the moment the connection goes, and stops being one
   * when it comes back — so the message follows the truth rather than a timer.
   */
  setConnectionUp(up: boolean): void {
    if (this.#connectionUp === up) return;
    this.#connectionUp = up;

    // Only meaningful while something is still outstanding.
    if (this.#status === "slow" && !up) {
      this.#status = "lost";
      this.#relayout();
    } else if (this.#status === "lost" && up) {
      this.#status = "slow";
      this.#relayout();
    }
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
    if (this.#mode === "settings") return;
    // The surface form, not the definition's term: a translation can be asked for before the
    // definition has arrived, and there is no term yet to ask with (D32).
    if (this.#surfaceForm) this.#callbacks?.onTranslate(this.#surfaceForm);
  }

  /** The `e` key, and the toolbar's settings button. */
  editSettings(): void {
    this.#callbacks?.onEditSettings();
  }

  requestClose(): void {
    this.#callbacks?.onClose();
  }

  /** Shown between asking for a translation and the server answering. */
  showTranslationPending(): void {
    if (!this.#root) return;

    this.#translationPending = true;
    this.#translation = undefined;
    this.#relayout();
    this.#revealTranslation();
  }

  /** The server's answer. Re-renders so the new block is in place under the sense. */
  showTranslation(translation: TranslationView): void {
    if (!this.#root) return;

    this.#translationPending = false;
    this.#translation = translation;
    this.#relayout();
    this.#revealTranslation();
  }

  /**
   * Scrolls the translation into the three lines the body actually shows. It is appended
   * below the sense, so in a frame this short it arrives out of sight — and a translation the
   * reader has to go looking for is most of the way to no translation at all.
   */
  #revealTranslation(): void {
    const block = this.#body?.querySelector(".definition-box__translation");
    block?.scrollIntoView({ block: "nearest" });
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

  /** The body, in whichever of the three states the lookup is in — or the settings form. */
  #renderBody(): void {
    const body = this.#body;
    if (!body) return;

    body.replaceChildren();

    // The form needs more than the three lines a definition gets (D27); the height rule is
    // relaxed for it in CSS rather than measured here.
    body.classList.toggle("definition-box__body--form", this.#mode === "settings");

    if (this.#mode === "settings") {
      this.#renderSettingsForm(body);
      return;
    }

    if (this.#status !== "ready" || !this.#view) {
      const waiting = document.createElement("p");
      // Red is reserved for the case that is genuinely wrong; waiting is just waiting (D33).
      waiting.className = this.#status === "lost"
        ? "definition-box__error"
        : "definition-box__loading";
      waiting.textContent = this.#status === "lost"
        ? lostMessage
        : this.#status === "slow" ? slowMessage : "loading…";
      body.appendChild(waiting);

      if (this.#counter) this.#counter.textContent = "";

      // A translation asked for alongside the definition arrives first and is shown first;
      // the definition keeps its own `loading…` line above it until it lands (D32).
      this.#appendTranslation(body);
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

  /**
   * The settings form: an email, then a filtered list of languages.
   *
   * The keys follow the navigation menu's habit — type to narrow, Enter to go forward, Escape
   * to back out — so this behaves like the rest of the app rather than like a web form.
   */
  #renderSettingsForm(body: HTMLElement): void {
    const settings = this.#settings;
    if (!settings) return;

    // One line, and no explaining. The reader of this app is the person who built it and does
    // not need to be told why the email is wanted — and on a phone every line of prose here is
    // a line of the language list pushed behind the soft keyboard.
    const intro = document.createElement("p");
    intro.className = "definition-box__form-intro";
    intro.textContent = "Translation needs an email and a language.";
    body.appendChild(intro);

    // ---- email --------------------------------------------------------------------
    const email = document.createElement("input");
    email.type = "email";
    email.className = "definition-box__field";
    email.placeholder = "you@example.com";
    email.value = settings.email ?? "";
    email.autocomplete = "email";
    email.spellcheck = false;
    email.setAttribute("aria-label", "Email for the translation service");
    email.addEventListener("keydown", (event) => this.#handleEmailKey(event));
    body.appendChild(this.#labelled("email", email));
    this.#emailField = email;

    // Directly under the email, not at the foot of the form: on a phone the soft keyboard
    // covers everything below the fields, and a refusal nobody can see is no refusal at all.
    const message = document.createElement("p");
    message.className = "definition-box__form-message";
    message.setAttribute("role", "alert");
    body.appendChild(message);
    this.#formMessage = message;

    // ---- language: a filter above the list it filters -------------------------------
    const filter = document.createElement("input");
    filter.type = "text";
    filter.className = "definition-box__field";
    filter.placeholder = "type to filter…";
    filter.spellcheck = false;
    filter.setAttribute("aria-label", "Filter languages");
    filter.addEventListener("input", () => this.#renderLanguageOptions(filter.value));
    filter.addEventListener("keydown", (event) => this.#handleFilterKey(event));
    body.appendChild(this.#labelled("language", filter));
    this.#languageFilter = filter;

    const list = document.createElement("select");
    list.className = "definition-box__list";
    list.size = languageRows;
    list.setAttribute("aria-label", "Language to translate into");
    list.addEventListener("keydown", (event) => this.#handleListKey(event));
    // Deliberately no click-to-submit. Tapping a <select> on Android opens the browser's own
    // language picker, so a click handler here fired at the same time and left that picker
    // stranded over the page. Touch picks from the native list and taps `save`; a keyboard
    // presses Enter. One gesture each, and no fight with the browser.
    body.appendChild(list);
    this.#languageList = list;

    this.#renderLanguageOptions("");
  }

  #labelled(text: string, field: HTMLElement): HTMLElement {
    const row = document.createElement("label");
    row.className = "definition-box__form-row";

    const caption = document.createElement("span");
    caption.className = "definition-box__form-label";
    caption.textContent = text;

    row.appendChild(caption);
    row.appendChild(field);
    return row;
  }

  /** Rebuilds the list from the filter, keeping the stored language selected when it survives. */
  #renderLanguageOptions(filterText: string): void {
    const list = this.#languageList;
    const settings = this.#settings;
    if (!list || !settings) return;

    const needle = filterText.trim().toLowerCase();
    const matches = settings.languages.filter((language) =>
      needle.length === 0
      || language.name.toLowerCase().includes(needle)
      || language.code.toLowerCase().startsWith(needle));

    list.replaceChildren();
    for (const language of matches) {
      const option = document.createElement("option");
      option.value = language.code;
      option.textContent = `${language.name}  (${language.code})`;
      list.appendChild(option);
    }

    const stored = matches.findIndex((language) => language.code === settings.language);
    list.selectedIndex = matches.length === 0 ? -1 : Math.max(0, stored);
  }

  #handleEmailKey(event: KeyboardEvent): void {
    if (event.key === "Escape") {
      event.preventDefault();
      this.#settingsCallbacks?.onCancel();
      return;
    }

    if (event.key !== "Enter") return;
    event.preventDefault();

    if (!this.#validateEmail()) return;

    this.#showFormMessage("");
    this.#languageFilter?.focus();
  }

  #handleFilterKey(event: KeyboardEvent): void {
    if (event.key === "Escape") {
      event.preventDefault();
      this.#settingsCallbacks?.onCancel();
      return;
    }

    // Enter and Down both hand over to the list, which is where the choosing happens.
    if (event.key !== "Enter" && event.key !== "ArrowDown") return;
    event.preventDefault();

    const list = this.#languageList;
    if (!list || list.options.length === 0) {
      this.#showFormMessage("No language matches that.");
      return;
    }

    if (list.selectedIndex < 0) list.selectedIndex = 0;
    list.focus();
  }

  #handleListKey(event: KeyboardEvent): void {
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
    const value = (this.#emailField?.value ?? "").trim();

    if (!emailPattern.test(value)) {
      this.#showFormMessage("That does not look like an email address.");
      this.#emailField?.focus();
      return false;
    }

    return true;
  }

  #submitSettings(): void {
    if (!this.#validateEmail()) return;

    const language = this.#languageList?.value ?? "";
    if (language.length === 0) {
      this.#showFormMessage("Pick a language from the list.");
      this.#languageFilter?.focus();
      return;
    }

    const email = (this.#emailField?.value ?? "").trim();
    this.#settingsCallbacks?.onSubmit(email, language);
  }

  /** Errors are written into the form rather than re-rendering it, so focus stays put. */
  #showFormMessage(text: string): void {
    if (this.#formMessage) this.#formMessage.textContent = text;
  }

  /** Reports a refusal that came back from the server, and puts the reader on the bad field. */
  showSettingsError(message: string, field: "email" | "language"): void {
    this.#showFormMessage(message);
    if (field === "email") {
      this.#emailField?.focus();
    } else {
      this.#languageFilter?.focus();
    }
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
    const failure = this.#translation?.error;
    text.className = failure
      ? "definition-box__translation-text definition-box__translation-text--failed"
      : "definition-box__translation-text";
    text.textContent = this.#translationPending
      ? "translating…"
      : (failure ?? this.#translation?.text ?? "");
    block.appendChild(text);

    const note = this.#translation?.error ? null : this.#translation?.note;
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

    if (this.#mode === "settings") {
      hints.textContent = "enter next  ·  esc cancel";
      return;
    }

    const view = this.#view;
    const parts: string[] = [];

    // Nothing but Escape means anything until there is a definition to act on — except the
    // translation, which may already be here or on its way (D32).
    if (!view) {
      hints.textContent = this.#translationPending ? "translating…  ·  esc close" : "esc close";
      return;
    }

    if (view.senses.length > 1) parts.push("j/k senses");
    parts.push(this.#isSaved ? "d delete" : "s save");
    parts.push(this.#translationPending ? "translating…" : "t translate");
    parts.push("e settings");
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

    if (this.#mode === "settings") {
      add("save", "Save translation settings", () => this.#submitSettings());
      add("cancel", "Cancel", () => this.#settingsCallbacks?.onCancel());
      return;
    }

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

    // The phone's `e`: without it a touch reader could set translation up once and never
    // change it (D31).
    add("settings", "Translation settings", () => this.#callbacks?.onEditSettings());

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

    root.style.top = `${Math.max(viewportMargin, top)}px`;
    root.style.left = `${left}px`;

    // Tail sits over the word rather than the middle of a shifted panel.
    const tailOffset = Math.max(16, Math.min(anchorCentre - left, panelWidth - 16));
    root.style.setProperty("--tail-offset", `${tailOffset}px`);
  }
}
