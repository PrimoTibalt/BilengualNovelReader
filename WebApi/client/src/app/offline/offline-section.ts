import {
  ChangeDetectionStrategy,
  Component,
  type ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from "@angular/core";
import { ProgressDots } from "../core/progress-dots";
import { OfflineStore } from "../core/offline-store";
import { SettingsPanel } from "../settings/settings-panel";
import { ChapterDownloads, type DownloadProgress, maximumChaptersPerDownload, parseChapterList } from "./chapter-downloads";

const defaultAhead = 10;
const aheadKey = "novelreader.offline.ahead";

/** `[1, 2, 3, 5, 8, 9]` as `1–3, 5, 8–9`. */
export function describeRanges(numbers: readonly number[]): string {
  const ranges: string[] = [];
  for (let index = 0; index < numbers.length; ) {
    let end = index;
    while (end + 1 < numbers.length && numbers[end + 1] === numbers[end]! + 1) end++;
    ranges.push(end === index ? `${numbers[index]}` : `${numbers[index]}–${numbers[end]}`);
    index = end + 1;
  }
  return ranges.join(", ");
}

function megabytes(bytes: number): string {
  return bytes < 1024 * 1024 ? "under 1 MB" : `${Math.round(bytes / (1024 * 1024))} MB`;
}

/**
 * Chapters to keep on this device for reading offline (D51), for the novel being read: the one
 * open and the next so many, or exactly the chapters typed (`5-12, 20`). It says which are
 * already here, and can remove them. The download itself runs in {@link ChapterDownloads}, so it
 * carries on with the drawer closed.
 */
@Component({
  selector: "app-offline-section",
  imports: [ProgressDots],
  templateUrl: "./offline-section.html",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OfflineSection {
  readonly #downloads = inject(ChapterDownloads);
  readonly #store = inject(OfflineStore);

  /** The novel being read, its title for the status line, and the chapter on screen. */
  readonly novel = input.required<string>();
  readonly novelTitle = input<string | null>(null);
  readonly chapter = input.required<number>();
  /** Known for an uploaded book, so "ahead" stops at its last chapter. */
  readonly totalChapters = input<number | null>(null);

  protected readonly maximum = maximumChaptersPerDownload;
  protected readonly progress = this.#downloads.progress;
  protected readonly running = computed(() => {
    const progress = this.progress();
    return progress !== undefined && progress.outcome === undefined;
  });

  protected readonly stored = signal<readonly number[]>([]);
  protected readonly usedBytes = signal<number | undefined>(undefined);
  protected readonly note = signal("");

  protected readonly ahead = viewChild.required<ElementRef<HTMLInputElement>>("ahead");
  protected readonly list = viewChild.required<ElementRef<HTMLInputElement>>("list");

  protected readonly status = computed(() => {
    const stored = this.stored();
    const title = this.novelTitle() ?? this.novel();
    if (stored.length === 0) return `Nothing of “${title}” is on this device yet.`;

    const used = this.usedBytes();
    const total = used === undefined ? "" : ` · ${megabytes(used)} kept in all`;
    return `${stored.length} ${stored.length === 1 ? "chapter" : "chapters"} of “${title}” on this device: ${describeRanges(stored)}${total}.`;
  });

  protected readonly progressText = computed(() => describeProgress(this.progress()));

  constructor() {
    const panel = inject(SettingsPanel);

    // Looked again at whenever the drawer opens, the novel changes or a chapter lands.
    effect(() => {
      panel.openings();
      this.#downloads.changes();
      const novel = this.novel();
      untracked(() => void this.#refresh(novel));
    });

    effect(() => {
      panel.openings();
      untracked(() => {
        this.note.set("");
        this.#downloads.clearOutcome();
      });
    });
  }

  protected downloadAhead(): void {
    const count = Number(this.ahead().nativeElement.value);
    if (!Number.isInteger(count) || count < 0 || count > maximumChaptersPerDownload) {
      this.note.set(`Enter a number of chapters from 0 to ${maximumChaptersPerDownload}.`);
      return;
    }

    try {
      localStorage.setItem(aheadKey, String(count));
    } catch {
      // Not remembered; the default comes back next time.
    }

    const first = Math.max(1, this.chapter());
    const total = this.totalChapters();
    const last = total === null ? first + count : Math.min(total, first + count);
    const chapters = Array.from({ length: last - first + 1 }, (_, index) => first + index);
    this.#start(chapters, true);
  }

  protected downloadList(): void {
    const chapters = parseChapterList(this.list().nativeElement.value);
    if (chapters === undefined) {
      this.note.set(`Chapter numbers and ranges, like 5-12, 20 — at most ${maximumChaptersPerDownload}.`);
      return;
    }

    this.#start(chapters, false);
  }

  protected cancel(): void {
    this.#downloads.cancel();
  }

  protected async remove(): Promise<void> {
    if (this.running()) return;
    await this.#store.removeNovel(this.novel());
    this.note.set("Removed from this device.");
    await this.#refresh(this.novel());
  }

  protected defaultAhead(): string {
    try {
      return localStorage.getItem(aheadKey) ?? String(defaultAhead);
    } catch {
      return String(defaultAhead);
    }
  }

  #start(chapters: number[], stopAtEnd: boolean): void {
    if (this.running()) return;
    this.note.set("");
    void this.#downloads.download(this.novel(), chapters, stopAtEnd);
  }

  async #refresh(novel: string): Promise<void> {
    if (novel === "") return;

    const [stored, used] = await Promise.all([this.#store.chapterNumbers(novel), this.#store.usedBytes()]);
    if (this.novel() !== novel) return;
    this.stored.set(stored);
    this.usedBytes.set(used);
  }
}

function describeProgress(progress: DownloadProgress | undefined): string {
  if (progress === undefined) return "";

  const counts = `${progress.done}/${progress.total}`;
  const failed = progress.failed > 0 ? ` · ${progress.failed} failed` : "";
  switch (progress.outcome) {
    case undefined:
      return progress.current === undefined
        ? `checking ${counts}`
        : `downloading chapter ${progress.current} · ${counts}${failed}`;
    case "finished":
      return progress.downloaded === 0 && progress.failed === 0
        ? "All of those were already on this device."
        : `Downloaded ${progress.downloaded}${failed}.`;
    case "end":
      return `Downloaded ${progress.downloaded}${failed} — there is no chapter ${progress.endedAt} yet.`;
    case "cancelled":
      return `Stopped after ${progress.downloaded} ${progress.downloaded === 1 ? "chapter" : "chapters"}.`;
    case "offline":
      return `The connection dropped after ${progress.downloaded}; download again once it is back.`;
  }
}
