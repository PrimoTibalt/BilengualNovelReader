import { Injectable, inject, signal } from "@angular/core";
import { OfflineStore } from "../core/offline-store";
import { ReaderConnection } from "../core/reader-connection";

/** The most chapters one download takes, whichever way they were asked for. */
export const maximumChaptersPerDownload = 500;

/** What the drawer shows while a download runs, and once it has finished. */
export interface DownloadProgress {
  readonly novel: string;
  /** Chapters asked for, those already on the device included. */
  readonly total: number;
  /** Chapters dealt with so far: downloaded, already there, or failed. */
  readonly done: number;
  readonly downloaded: number;
  readonly failed: number;
  /** The chapter on its way, while one is. */
  readonly current: number | undefined;
  /** Set once it is over: `finished`, `cancelled`, `end` (the novel ran out), `offline`. */
  readonly outcome: "finished" | "cancelled" | "end" | "offline" | undefined;
  /** With `end`: the first chapter that does not exist. */
  readonly endedAt: number | undefined;
}

/**
 * `5-12, 20, 31-33` into the chapter numbers it names, in order, each once. Undefined for
 * anything that is not such a list, or one that names more than {@link maximumChaptersPerDownload}.
 */
export function parseChapterList(text: string): number[] | undefined {
  const numbers = new Set<number>();
  const parts = text.split(/[,;\s]+/).filter((part) => part.length > 0);
  if (parts.length === 0) return undefined;

  for (const part of parts) {
    const range = /^(\d{1,6})(?:\s*[-–]\s*(\d{1,6}))?$/.exec(part);
    if (!range) return undefined;

    const from = Number(range[1]);
    const to = range[2] === undefined ? from : Number(range[2]);
    if (from < 1 || to < from || to - from >= maximumChaptersPerDownload) return undefined;

    for (let chapter = from; chapter <= to; chapter++) numbers.add(chapter);
    if (numbers.size > maximumChaptersPerDownload) return undefined;
  }

  return [...numbers].sort((left, right) => left - right);
}

/**
 * Downloads chapters to the device for reading offline (D51), one at a time.
 *
 * One at a time is the point: on the server each one takes the reader's turn, so even a long
 * download reaches the source site a chapter at a time (D17), and the reader's own loads simply
 * queue between them. A chapter already on the device is not fetched again. A chapter the site
 * does not have ends a download "ahead" — the novel has run out — and is passed over in a list.
 *
 * A service rather than part of the drawer, so closing the drawer does not stop a download.
 */
@Injectable({ providedIn: "root" })
export class ChapterDownloads {
  readonly #connection = inject(ReaderConnection);
  readonly #store = inject(OfflineStore);

  readonly #progress = signal<DownloadProgress | undefined>(undefined);
  readonly progress = this.#progress.asReadonly();

  /** Bumped when a chapter is added, so whatever lists them can look again. */
  readonly #changes = signal(0);
  readonly changes = this.#changes.asReadonly();

  #cancelled = false;

  get running(): boolean {
    const progress = this.#progress();
    return progress !== undefined && progress.outcome === undefined;
  }

  cancel(): void {
    this.#cancelled = true;
  }

  /** Forgets the outcome line once the reader has seen it. */
  clearOutcome(): void {
    if (!this.running) this.#progress.set(undefined);
  }

  /**
   * Downloads `chapters` of `novel`. With `stopAtEnd`, the first chapter the novel does not have
   * ends it: that is "the next N chapters" running off the end of what is published.
   */
  async download(novel: string, chapters: readonly number[], stopAtEnd: boolean): Promise<void> {
    if (this.running || chapters.length === 0) return;

    this.#cancelled = false;
    let progress: DownloadProgress = {
      novel, total: chapters.length, done: 0, downloaded: 0, failed: 0, current: undefined, outcome: undefined, endedAt: undefined,
    };
    const update = (change: Partial<DownloadProgress>): void => {
      progress = { ...progress, ...change };
      this.#progress.set(progress);
    };
    update({});

    // Asked once, at the first download: an installed app is usually granted it.
    void this.#store.persist();

    for (const chapter of chapters) {
      if (this.#cancelled) {
        update({ current: undefined, outcome: "cancelled" });
        return;
      }

      if (await this.#store.hasChapter(novel, chapter)) {
        update({ done: progress.done + 1 });
        continue;
      }

      if (!this.#connection.isConnected()) {
        update({ current: undefined, outcome: "offline" });
        return;
      }

      update({ current: chapter });
      const answer = await this.#connection.downloadChapter(novel, chapter);

      if (answer.found) {
        try {
          await this.#store.putChapter(answer);
          update({ done: progress.done + 1, downloaded: progress.downloaded + 1 });
          this.#changes.update((count) => count + 1);
        } catch (error: unknown) {
          // A picture that would not come: the chapter is not kept without it.
          console.error(`Chapter ${chapter} not kept:`, error);
          update({ done: progress.done + 1, failed: progress.failed + 1 });
        }
        continue;
      }

      const missing = !answer.failed && answer.failure !== "unavailable";
      if (missing && stopAtEnd) {
        update({ current: undefined, outcome: "end", endedAt: chapter });
        return;
      }

      update({ done: progress.done + 1, failed: progress.failed + (missing ? 0 : 1) });
    }

    update({ current: undefined, outcome: "finished" });
  }
}
