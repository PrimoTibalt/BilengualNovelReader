import { ChangeDetectionStrategy, Component } from "@angular/core";

/**
 * `.  ` → `.. ` → `...` → `.  `, for as long as it is on the page: something has been asked of
 * the server and has not come back yet (D48). Drawn by CSS alone (`.progress-dots` in
 * `tui.css`), so an idle page runs no timer. Three dots always take their room, so the text
 * after it does not shuffle; the dots are decoration, and the words beside them say what is
 * happening.
 */
@Component({
  selector: "app-progress-dots",
  template: `<span class="progress-dots" aria-hidden="true"><span>.</span><span>.</span><span>.</span></span>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProgressDots {}
