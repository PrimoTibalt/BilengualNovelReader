import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from "@angular/core";
import {
  HTTP_FILTER,
  HUB_FILTER,
  RANGES,
  SignedOut,
  adminUrl,
  getOverview,
  getTimings,
  type Overview,
  type Range,
  type Timings,
} from "./admin-api";
import { LatencyChart } from "./latency-chart";
import { ago, clockWithSeconds, device, duration, isBadOutcome, moment, outcomeLabel } from "./format";

/** How often the panel asks again on its own, while it is visible. */
const REFRESH_INTERVAL_MS = 30_000;

/** The outcomes counted in the header of the sign-in section, in this order. */
const COUNTED_OUTCOMES = ["sign-in-failed", "sign-in-locked", "rate-limited", "signed-in"] as const;

/**
 * The admin panel (D45): response times, who is online, sign-in attempts, the readers' words
 * and novels, and the errors the server logged. A separate build from the reader's page, served
 * by AdminController to admins only. It reads two JSON endpoints and refreshes them every 30 s
 * while the tab is visible.
 */
@Component({
  selector: "admin-root",
  imports: [LatencyChart],
  templateUrl: "./admin-page.html",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AdminPage {
  protected readonly ranges = RANGES;
  protected readonly countedOutcomes = COUNTED_OUTCOMES;
  protected readonly readerUrl = adminUrl("../ReadingPage");

  protected readonly overview = signal<Overview | null>(null);
  protected readonly timings = signal<Timings | null>(null);
  protected readonly range = signal<Range>("24h");
  protected readonly filter = signal<string>(HUB_FILTER);
  protected readonly loading = signal(false);
  protected readonly failure = signal<string | null>(null);
  protected readonly updatedAt = signal<string | null>(null);

  /** The groups, then every operation — keeping the chosen one even when the range has none of it. */
  protected readonly filterOptions = computed(() => {
    const options = [
      { value: HUB_FILTER, label: "all hub calls" },
      { value: HTTP_FILTER, label: "all HTTP requests" },
    ];
    const operations = (this.timings()?.operations ?? []).map((operation) => operation.operation);
    const chosen = this.filter();
    if (chosen !== HUB_FILTER && chosen !== HTTP_FILTER && !operations.includes(chosen)) {
      operations.unshift(chosen);
    }
    return [...options, ...operations.map((operation) => ({ value: operation, label: operation }))];
  });

  protected readonly onlineCount = computed(() => this.overview()?.online.length ?? 0);
  protected readonly connectionCount = computed(
    () => this.overview()?.online.reduce((total, reader) => total + reader.connections.length, 0) ?? 0,
  );
  protected readonly failedLastDay = computed(() => {
    const counts = this.overview()?.signIns.lastDay ?? {};
    return Object.entries(counts).reduce((total, [code, count]) => total + (isBadOutcome(code) ? count : 0), 0);
  });
  protected readonly savedWords = computed(
    () => this.overview()?.readers.reduce((total, reader) => total + reader.savedWords, 0) ?? 0,
  );
  protected readonly errorsLastDay = computed(() => {
    const since = Date.now() - 24 * 60 * 60 * 1000;
    return (this.overview()?.errors ?? []).filter((error) => new Date(error.at).getTime() >= since).length;
  });

  protected readonly duration = duration;
  protected readonly moment = moment;
  protected readonly ago = ago;
  protected readonly device = device;
  protected readonly outcomeLabel = outcomeLabel;
  protected readonly isBadOutcome = isBadOutcome;

  constructor() {
    void this.refresh();

    const timer = setInterval(() => {
      if (document.visibilityState === "visible") void this.refresh();
    }, REFRESH_INTERVAL_MS);
    // Back to a tab left in the background: catch up at once rather than at the next tick.
    const onVisible = (): void => {
      if (document.visibilityState === "visible") void this.refresh();
    };
    document.addEventListener("visibilitychange", onVisible);
    inject(DestroyRef).onDestroy(() => {
      clearInterval(timer);
      document.removeEventListener("visibilitychange", onVisible);
    });
  }

  protected async refresh(): Promise<void> {
    if (this.loading()) return;
    this.loading.set(true);
    try {
      const [overview, timings] = await Promise.all([
        getOverview(),
        getTimings(this.range(), this.filter()),
      ]);
      this.overview.set(overview);
      this.timings.set(timings);
      this.updatedAt.set(overview.generatedAt);
      this.failure.set(null);
    } catch (error) {
      this.#fail(error);
    } finally {
      this.loading.set(false);
    }
  }

  protected chooseRange(range: Range): void {
    this.range.set(range);
    void this.#reloadTimings();
  }

  protected chooseFilter(filter: string): void {
    this.filter.set(filter);
    void this.#reloadTimings();
  }

  protected clockWithSeconds(iso: string | null): string {
    return iso === null ? "never" : clockWithSeconds(iso);
  }

  /** The category's last part — `RealTimeReaderHub` rather than the whole namespace. */
  protected shortCategory(category: string): string {
    return category.slice(category.lastIndexOf(".") + 1);
  }

  protected firstLine(text: string): string {
    const end = text.indexOf("\n");
    return end < 0 ? text : text.slice(0, end);
  }

  async #reloadTimings(): Promise<void> {
    try {
      this.timings.set(await getTimings(this.range(), this.filter()));
      this.failure.set(null);
    } catch (error) {
      this.#fail(error);
    }
  }

  #fail(error: unknown): void {
    if (error instanceof SignedOut) {
      window.location.assign(adminUrl("../Login"));
      return;
    }
    this.failure.set(error instanceof Error && error.message ? error.message : "Could not reach the server.");
  }
}
