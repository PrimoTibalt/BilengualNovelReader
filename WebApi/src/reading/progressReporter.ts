/**
 * Reports the reader's bookmark: the last paragraph they actually have on screen.
 *
 * Scroll events fire continuously, so reporting on each one would be a request per frame.
 * Instead every scroll restarts a timer and only the settled position is sent — one request
 * once the reader has stopped for {@link idleDelayInMilliseconds}, and none at all if they
 * scroll straight past (D19).
 */

export interface ReadingPosition {
  readonly chapterNumber: number;
  readonly paragraphNumber: number;
}

export type ReportPosition = (position: ReadingPosition) => void;

/** How long the reader must be still before their position counts as settled. */
const idleDelayInMilliseconds = 2000;

export class ProgressReporter {
  readonly #container: HTMLElement;
  readonly #report: ReportPosition;
  #timer: number | undefined;
  #lastReported: ReadingPosition | undefined;

  constructor(container: HTMLElement, report: ReportPosition) {
    this.#container = container;
    this.#report = report;

    // Leaving the page is the one moment worth reporting without waiting out the timer.
    document.addEventListener("visibilitychange", () => {
      if (document.visibilityState === "hidden") this.flush();
    });
  }

  /** Call on every scroll; only the last one in a burst does any work. */
  scheduleReport(): void {
    if (this.#timer !== undefined) clearTimeout(this.#timer);

    this.#timer = window.setTimeout(() => {
      this.#timer = undefined;
      this.flush();
    }, idleDelayInMilliseconds);
  }

  /** Reports immediately, if the position has actually moved since last time. */
  flush(): void {
    if (this.#timer !== undefined) {
      clearTimeout(this.#timer);
      this.#timer = undefined;
    }

    const position = this.currentPosition();
    if (!position) return;

    if (
      this.#lastReported?.chapterNumber === position.chapterNumber &&
      this.#lastReported?.paragraphNumber === position.paragraphNumber
    ) {
      return;
    }

    this.#lastReported = position;
    this.#report(position);
  }

  /**
   * Treats the bookmark as "already seen": a position that came from the server should not
   * be sent straight back to it.
   */
  markReported(position: ReadingPosition): void {
    this.#lastReported = position;
  }

  /**
   * The last paragraph that has begun above the bottom of the viewport — the deepest one the
   * reader can see. Paragraphs are in document order, so this walks back from the end and
   * stops at the first match rather than measuring all of them.
   */
  currentPosition(): ReadingPosition | undefined {
    const viewportBottom = window.innerHeight;
    const children = this.#container.children;

    for (let index = children.length - 1; index >= 0; index--) {
      const element = children[index];
      if (!(element instanceof HTMLElement)) continue;

      if (element.getBoundingClientRect().top >= viewportBottom) continue;

      const chapterNumber = Number(element.dataset["chapter"]);
      const paragraphNumber = Number(element.dataset["paragraph"]);
      if (!Number.isFinite(chapterNumber) || !Number.isFinite(paragraphNumber)) continue;

      return { chapterNumber, paragraphNumber };
    }

    return undefined;
  }
}
