import { Injectable, computed, signal } from "@angular/core";
import type { AssistantSettingsView, TranslationLanguageView } from "./hub-models";

const noAssistant: AssistantSettingsView = { services: [], serviceId: null, model: null, keyHint: null, error: null };

/**
 * What the server has told the page about the reader's own settings, shared by everything
 * that acts on them: the definition box, the drawer's sections and the review.
 */
@Injectable({ providedIn: "root" })
export class ReaderState {
  readonly assistant = signal<AssistantSettingsView>(noAssistant);

  /**
   * Whether the reader has an AI set up. While they do, every lookup carries the paragraph it
   * was made in, which the AI reads the selection against (D35) — and the review is offered.
   */
  readonly assistantEnabled = computed(() => this.assistant().serviceId !== null);

  /**
   * The reader's translation settings, as the server last told us. A null email means they
   * have never set translation up, which is what makes `t` open the form instead of asking for
   * a translation nobody can fetch (D31).
   */
  readonly translationEmail = signal<string | null>(null);
  readonly translationLanguage = signal<string | null>(null);
  readonly translationLanguages = signal<readonly TranslationLanguageView[]>([]);
}
