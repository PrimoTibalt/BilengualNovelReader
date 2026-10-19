/**
 * Smooth j/k scrolling of the reading column, and the lock that stops all scrolling while
 * the definition box is open.
 *
 * The scroll speed is driven by animation frames rather than OS key repeat, so holding a
 * key glides instead of stuttering.
 */

/** CSS pixels moved per animation frame while a scroll key is held. */
const scrollStepInPixels = 5;

export type ScrollDirection = "down" | "up";

export class SmoothScroller {
  #scrolling = false;
  #locked = false;
  #frameHandle: number | undefined;

  constructor() {
    // A key released while the box is open must not leave the scroller running.
    document.addEventListener("keyup", this.#handleKeyUp);
    window.addEventListener("blur", () => this.stop());
  }

  get isLocked(): boolean {
    return this.#locked;
  }

  start(direction: ScrollDirection): void {
    if (this.#locked || this.#scrolling) return;

    this.#scrolling = true;
    const step = direction === "down" ? scrollStepInPixels : -scrollStepInPixels;

    const scrollSmoothly = (): void => {
      if (!this.#scrolling || this.#locked) return;
      window.scrollBy(0, step);
      this.#frameHandle = requestAnimationFrame(scrollSmoothly);
    };

    this.#frameHandle = requestAnimationFrame(scrollSmoothly);
  }

  stop(): void {
    this.#scrolling = false;
    if (this.#frameHandle !== undefined) {
      cancelAnimationFrame(this.#frameHandle);
      this.#frameHandle = undefined;
    }
  }

  /**
   * Freezes the page. Stops the smooth scroller and also blocks wheel, touch and the
   * browser's own scroll keys, which the router never sees.
   */
  lock(): void {
    if (this.#locked) return;

    this.stop();
    this.#locked = true;
    document.documentElement.classList.add("scroll-locked");
  }

  unlock(): void {
    if (!this.#locked) return;

    this.#locked = false;
    document.documentElement.classList.remove("scroll-locked");
  }

  #handleKeyUp = (event: KeyboardEvent): void => {
    if (event.key === "j" || event.key === "k") this.stop();
  };
}
