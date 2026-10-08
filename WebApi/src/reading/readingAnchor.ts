/**
 * Keeps the reader on the line they were reading when the column is re-laid out under them —
 * the phone turned, or the window resized (D36). A font-size change is handled by the page
 * instead, which keeps the paragraph at the *bottom* of the screen in place.
 *
 * The place is recorded *continuously*, while the layout is still the one the reader was
 * looking at, and put back from that record once the layout changes. Reading the place at the
 * moment of the change is what went wrong before: by the time a phone that was switched off
 * in portrait and back on in landscape tells the page so, the text has already re-wrapped at
 * the new width under the old scroll offset, so whatever was "on screen" then was a paragraph
 * the reader had never seen — and the page dutifully scrolled to it.
 *
 * The anchor is the paragraph at the *top* of the viewport and how far into it the reader had
 * scrolled, as a fraction of its height. A fraction survives re-wrapping: a paragraph that was
 * four lines and is now seven is still a third read when it was a third read.
 */

interface Anchor {
  readonly element: HTMLElement;
  /** How much of the paragraph had scrolled off the top, 0–1. */
  readonly fraction: number;
}

/**
 * How long after a re-layout the page keeps putting the reader back. Phones re-lay the page in
 * more than one step — the rotation, then the toolbar settling, then late font metrics — and
 * the scroll events those steps cause must not be mistaken for the reader scrolling.
 */
const settleWindowInMilliseconds = 700;

/** When within that window the place is put back again. */
const restoreDelaysInMilliseconds = [0, 120, 350, settleWindowInMilliseconds];

export class ReadingAnchor {
  readonly #container: HTMLElement;
  #anchor: Anchor | undefined;
  /** The column width the anchor was taken at. Only a change in width re-wraps the text. */
  #width: number;
  /** Until when scroll events are the page settling rather than the reader moving. */
  #settlingUntil = 0;
  #timers: number[] = [];

  constructor(container: HTMLElement) {
    this.#container = container;
    this.#width = container.clientWidth;

    window.addEventListener("scroll", () => this.#record(), { passive: true });

    // Any of these can be the first the page hears of a new width. `resize` is the reliable
    // one; the others cover a phone that was turned while the page was hidden, where the
    // resize can arrive before the page is visible again — or not at all.
    window.addEventListener("resize", () => this.#onPossibleRelayout());
    window.addEventListener("orientationchange", () => this.#onPossibleRelayout());
    window.addEventListener("pageshow", () => this.#onPossibleRelayout());
    document.addEventListener("visibilitychange", () => {
      if (document.visibilityState === "visible") this.#onPossibleRelayout();
    });
  }

  /** Records the place now — after the page itself has scrolled somewhere on purpose. */
  record(): void {
    this.#settlingUntil = 0;
    this.#record(true);
  }

  #onPossibleRelayout(): void {
    const width = this.#container.clientWidth;
    // Height changes — a toolbar sliding away — move nothing inside the column.
    if (width === this.#width || width === 0) return;

    this.#width = width;
    this.#restoreRepeatedly();
  }

  #restoreRepeatedly(): void {
    if (!this.#anchor) return;

    for (const timer of this.#timers) clearTimeout(timer);
    this.#timers = [];
    this.#settlingUntil = performance.now() + settleWindowInMilliseconds + 50;

    this.#restore();
    // Timers, not animation frames: a hidden page never paints, and a phone that was turned
    // while switched off is exactly a hidden page (D23).
    for (const delay of restoreDelaysInMilliseconds) {
      this.#timers.push(window.setTimeout(() => this.#restore(), delay));
    }
  }

  #restore(): void {
    const anchor = this.#anchor;
    if (!anchor || !anchor.element.isConnected) return;

    const rect = anchor.element.getBoundingClientRect();
    window.scrollTo({ top: window.scrollY + rect.top + anchor.fraction * rect.height });
  }

  #record(force = false): void {
    if (!force && performance.now() < this.#settlingUntil) return;
    // A width the anchor was not taken at means the re-layout has not been handled yet; the
    // scroll it caused says nothing about where the reader is.
    if (this.#container.clientWidth !== this.#width) return;

    const anchor = this.#topParagraph();
    if (anchor) this.#anchor = anchor;
  }

  /**
   * The first paragraph still showing at the top of the viewport, and how far into it.
   * Paragraphs are in document order, so their bottoms only grow: a binary search finds it in
   * a dozen measurements rather than one per paragraph on every scroll event.
   */
  #topParagraph(): Anchor | undefined {
    const children = this.#container.children;
    let low = 0;
    let high = children.length - 1;
    let found: HTMLElement | undefined;

    while (low <= high) {
      const middle = (low + high) >> 1;
      const child = children[middle];
      if (!(child instanceof HTMLElement)) return undefined;

      if (child.getBoundingClientRect().bottom > 0) {
        found = child;
        high = middle - 1;
      } else {
        low = middle + 1;
      }
    }

    if (!found) return undefined;

    const rect = found.getBoundingClientRect();
    const fraction = rect.height > 0 ? Math.min(1, Math.max(0, -rect.top / rect.height)) : 0;
    return { element: found, fraction };
  }
}
