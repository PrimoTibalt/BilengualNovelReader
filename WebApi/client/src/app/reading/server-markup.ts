import { Directive, ElementRef, type OnInit, inject, input } from "@angular/core";

/**
 * A paragraph made only of separator characters — the source site marks a scene change with a
 * long run of dashes. Recognised so it can be drawn as a rule rather than read as text (D34).
 */
const sceneBreakPattern = /^[-–—_=~*·•\s]{4,}$/;

/**
 * Writes a paragraph's markup into its element, once.
 *
 * The server escapes every scraped character and emits only its own tags (D3), so this is
 * markup the server authored, not text the novel site controls — which is why it bypasses
 * Angular's sanitiser, which would strip the `data-term` the saved-word spans carry. The CSP
 * is the backstop (D42). Written once and never re-bound: after this, the underliner patches
 * the text in place as words are saved and deleted (D14).
 */
@Directive({ selector: "[appServerMarkup]" })
export class ServerMarkup implements OnInit {
  readonly appServerMarkup = input.required<string>();
  readonly #element = inject<ElementRef<HTMLElement>>(ElementRef);

  ngOnInit(): void {
    const element = this.#element.nativeElement;
    element.innerHTML = this.appServerMarkup();

    if (sceneBreakPattern.test(element.textContent ?? "")) {
      element.classList.add("scene-break");
    }
  }
}
