import { ChangeDetectionStrategy, Component, computed, effect, inject, output, signal, untracked } from "@angular/core";
import type { ReviewSummaryView } from "../core/hub-models";
import { ReaderConnection } from "../core/reader-connection";
import { ReaderState } from "../core/reader-state";
import { SettingsPanel } from "../settings/settings-panel";
import { relativeTime } from "./relative-time";

/**
 * The drawer's first section: what the review has waiting, and the way in. Shown only to a
 * reader with an AI set up, because the review needs one throughout — to write the matching
 * definitions and to judge answers given in the reader's own words.
 */
@Component({
  selector: "app-review-section",
  template: `
    @if (enabled()) {
      <section class="settings-section">
        <h2 class="settings-section__title">review</h2>
        <p class="settings-status" [class.settings-status--on]="(summary()?.due ?? 0) + (summary()?.new ?? 0) > 0">{{ status() }}</p>
        <div class="settings-actions">
          <button type="button" class="settings-button" (click)="start.emit()">start review</button>
        </div>
      </section>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewSection {
  readonly #connection = inject(ReaderConnection);

  readonly start = output<void>();

  protected readonly enabled = inject(ReaderState).assistantEnabled;
  protected readonly summary = signal<ReviewSummaryView | undefined>(undefined);

  protected readonly status = computed(() => {
    const summary = this.summary();
    if (!summary) return "counting…";
    if (summary.error !== null) return "could not reach the server.";
    if (summary.total === 0) return "save words while you read, and they are reviewed here.";
    if (summary.due + summary.new === 0) {
      return summary.nextDueUtc === null ? "nothing due." : `nothing due — next ${relativeTime(summary.nextDueUtc)}.`;
    }

    return [summary.due > 0 ? `${summary.due} due` : null, summary.new > 0 ? `${summary.new} new` : null]
      .filter((part) => part !== null)
      .join(" · ");
  });

  constructor() {
    // Counted afresh each time the drawer opens, and when the AI is first set up.
    const panel = inject(SettingsPanel);
    effect(() => {
      panel.openings();
      if (this.enabled()) untracked(() => void this.#refresh());
    });
  }

  async #refresh(): Promise<void> {
    this.summary.set(undefined);
    this.summary.set(await this.#connection.getReviewSummary());
  }
}
