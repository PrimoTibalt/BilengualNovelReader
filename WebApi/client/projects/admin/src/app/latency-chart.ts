import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  inject,
  input,
  signal,
} from "@angular/core";
import type { TimingPoint } from "./admin-api";
import { clock, day, duration } from "./format";

/** The plot's margins, in px: room for the axis labels left and below. */
const MARGIN = { top: 10, right: 10, bottom: 24, left: 58 } as const;
const HEIGHT = 240;
/** The request-count bars along the bottom take this share of the plot's height. */
const COUNT_BAND = 0.22;

interface Tick {
  y: number;
  label: string;
}

interface Label {
  x: number;
  text: string;
}

/**
 * p50 and p95 over time, one point per slot, with the number of calls as faint bars beneath.
 * Drawn as SVG at the element's real width — measured, not scaled through a viewBox — so the
 * labels stay legible on a phone. Hovering (or touching) a slot reads it out above the plot.
 */
@Component({
  selector: "admin-latency-chart",
  templateUrl: "./latency-chart.html",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LatencyChart {
  readonly series = input.required<TimingPoint[]>();
  readonly slotMinutes = input.required<number>();

  protected readonly height = HEIGHT;
  protected readonly margin = MARGIN;
  protected readonly width = signal(0);
  protected readonly hovered = signal<number | null>(null);

  protected readonly plotWidth = computed(() => Math.max(0, this.width() - MARGIN.left - MARGIN.right));
  protected readonly plotHeight = HEIGHT - MARGIN.top - MARGIN.bottom;
  protected readonly plotBottom = HEIGHT - MARGIN.bottom;

  protected readonly empty = computed(() => this.series().every((point) => point.count === 0));

  /** A round number just above the slowest p95, so the gridlines land on round values. */
  readonly #top = computed(() => {
    const highest = Math.max(1, ...this.series().map((point) => point.p95 ?? 0));
    const magnitude = 10 ** Math.floor(Math.log10(highest));
    const step = [1, 2, 2.5, 5, 10].find((candidate) => candidate * magnitude >= highest) ?? 10;
    return step * magnitude;
  });

  readonly #slotWidth = computed(() => this.plotWidth() / Math.max(1, this.series().length));

  protected readonly ticks = computed<Tick[]>(() => {
    const top = this.#top();
    return [0, 0.25, 0.5, 0.75, 1].map((share) => ({
      y: this.#y(top * share),
      label: share === 0 ? "0" : duration(top * share),
    }));
  });

  protected readonly p50Path = computed(() => this.#path("p50"));
  protected readonly p95Path = computed(() => this.#path("p95"));

  protected readonly bars = computed(() => {
    const series = this.series();
    const most = Math.max(1, ...series.map((point) => point.count));
    const slot = this.#slotWidth();
    const band = this.plotHeight * COUNT_BAND;
    return series.flatMap((point, index) => {
      if (point.count === 0) return [];
      const height = Math.max(1, (point.count / most) * band);
      return [
        {
          x: MARGIN.left + index * slot + slot * 0.15,
          y: this.plotBottom - height,
          width: Math.max(1, slot * 0.7),
          height,
        },
      ];
    });
  });

  /** About one label per 110 px, on whole slots. */
  protected readonly labels = computed<Label[]>(() => {
    const series = this.series();
    if (series.length === 0) return [];
    const every = Math.max(1, Math.ceil(series.length / Math.max(1, Math.floor(this.plotWidth() / 110))));
    const labels: Label[] = [];
    for (let index = 0; index < series.length; index += every) {
      labels.push({ x: this.#x(index), text: this.#slotLabel(series[index]!.start) });
    }
    return labels;
  });

  protected readonly guide = computed(() => {
    const index = this.hovered();
    return index === null ? null : this.#x(index);
  });

  protected readonly readout = computed(() => {
    const index = this.hovered();
    const point = index === null ? undefined : this.series()[index];
    if (!point) return null;
    const end = new Date(new Date(point.start).getTime() + this.slotMinutes() * 60_000).toISOString();
    const when = this.slotMinutes() >= 24 * 60
      ? day(point.start)
      : `${this.#slotLabel(point.start)}–${clock(end)}`;
    return point.count === 0
      ? `${when} · no calls`
      : `${when} · ${point.count} ${point.count === 1 ? "call" : "calls"} · p50 ${duration(point.p50)} · p95 ${duration(point.p95)}`;
  });

  readonly #host = inject<ElementRef<HTMLElement>>(ElementRef);

  constructor() {
    const observer = new ResizeObserver(([entry]) => {
      if (entry) this.width.set(Math.floor(entry.contentRect.width));
    });
    observer.observe(this.#host.nativeElement);
    inject(DestroyRef).onDestroy(() => observer.disconnect());
  }

  protected onPointer(event: PointerEvent): void {
    const bounds = (event.currentTarget as SVGElement).getBoundingClientRect();
    const index = Math.floor((event.clientX - bounds.left - MARGIN.left) / this.#slotWidth());
    this.hovered.set(index >= 0 && index < this.series().length ? index : null);
  }

  protected onLeave(): void {
    this.hovered.set(null);
  }

  #x(index: number): number {
    return MARGIN.left + (index + 0.5) * this.#slotWidth();
  }

  #y(milliseconds: number): number {
    return this.plotBottom - (milliseconds / this.#top()) * this.plotHeight;
  }

  /**
   * One line through the slots that had calls, broken where a slot had none. A slot alone
   * between gaps is a zero-length segment, which the round line cap draws as a dot.
   */
  #path(key: "p50" | "p95"): string {
    let path = "";
    let drawing = false;
    this.series().forEach((point, index) => {
      const value = point[key];
      if (value === null) {
        drawing = false;
        return;
      }
      const x = this.#x(index).toFixed(1);
      const y = this.#y(value).toFixed(1);
      path += drawing ? ` L${x} ${y}` : ` M${x} ${y} L${x} ${y}`;
      drawing = true;
    });
    return path.trim();
  }

  #slotLabel(iso: string): string {
    const minutes = this.slotMinutes();
    if (minutes < 60) return clock(iso);
    if (minutes < 24 * 60 && new Date(iso).getHours() !== 0) return clock(iso);
    return day(iso);
  }
}
