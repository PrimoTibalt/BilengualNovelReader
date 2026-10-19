import { ChangeDetectionStrategy, Component, type ElementRef, afterRenderEffect, computed, inject, signal, untracked, viewChild } from "@angular/core";
import type { AssistantSettingsView } from "../core/hub-models";
import { ReaderConnection } from "../core/reader-connection";
import { ReaderState } from "../core/reader-state";
import { SettingsPanel } from "./settings-panel";

/** A model the service is known to have, shown as the field's placeholder — not a default. */
const exampleModels: Readonly<Record<string, string>> = {
  digitalocean: "glm-5.3",
  openai: "gpt-5-mini",
  anthropic: "claude-haiku-4-5",
  openrouter: "z-ai/glm-4.6",
  deepseek: "deepseek-chat",
  mistral: "mistral-small-latest",
  groq: "llama-3.3-70b-versatile",
};

/** The server's refusal codes, in the page's words. */
function describeError(code: string): string {
  switch (code) {
    case "service-unknown":
      return "Pick a service from the list.";
    case "model-invalid":
      return "Enter the model's id, without spaces.";
    case "key-invalid":
      return "Paste the whole API key.";
    default:
      return "Could not save — try again.";
  }
}

/**
 * The reader's own AI (D35): which service the key is for, which model to ask, and the key.
 * Once saved, definitions and translations come from that model, read against the paragraph
 * the selection was made in; `remove` forgets all three and the dictionaries answer again.
 *
 * The key is write-only. The server never sends it back — the field stays empty and the
 * status line names its last four characters — so leaving the field empty on a later save
 * keeps the key that is stored.
 */
@Component({
  selector: "app-assistant-section",
  templateUrl: "./assistant-section.html",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantSection {
  readonly #connection = inject(ReaderConnection);
  readonly #state = inject(ReaderState);

  protected readonly settings = this.#state.assistant;
  protected readonly configured = this.#state.assistantEnabled;
  protected readonly service = signal("");
  protected readonly note = signal("");
  protected readonly busy = signal(false);

  protected readonly model = viewChild.required<ElementRef<HTMLInputElement>>("model");
  protected readonly key = viewChild.required<ElementRef<HTMLInputElement>>("key");

  protected readonly status = computed(() => {
    const settings = this.settings();
    if (settings.serviceId === null) return "off — the dictionaries and MyMemory answer.";

    const serviceName = settings.services.find((service) => service.id === settings.serviceId)?.name
      ?? settings.serviceId;
    return `on — ${settings.model} via ${serviceName}, key …${settings.keyHint}`;
  });

  protected readonly modelPlaceholder = computed(() => {
    const example = exampleModels[this.service()];
    return example ? `e.g. ${example}` : "model id";
  });

  constructor() {
    // Brought up to date whenever the drawer opens, and whenever the server's answer changes.
    // After rendering, because the fields have to exist to be written to.
    const panel = inject(SettingsPanel);
    afterRenderEffect(() => {
      panel.openings();
      untracked(() => this.note.set(""));
    });
    afterRenderEffect(() => {
      panel.openings();
      this.settings();
      untracked(() => this.#refresh());
    });
  }

  protected async submit(): Promise<void> {
    if (this.busy()) return;

    const serviceId = this.service();
    const model = this.model().nativeElement.value.trim();
    const key = this.key().nativeElement.value.trim();

    // The server checks all of this again; these only save a round trip for the obvious.
    if (model.length === 0) {
      this.note.set("Enter the model to use.");
      this.model().nativeElement.focus();
      return;
    }
    if (key.length === 0 && this.settings().keyHint === null) {
      this.note.set("Paste the API key for this service.");
      this.key().nativeElement.focus();
      return;
    }

    await this.#whileBusy("saving…", async () => {
      const answer = await this.#connection.saveAssistantSettings(serviceId, model, key);
      if (!answer) {
        this.note.set("Could not reach the server — try again.");
        return;
      }

      if (answer.error !== null) {
        this.note.set(describeError(answer.error));
        return;
      }

      this.#accept(answer);
      this.note.set("Saved.");
    });
  }

  protected async forget(): Promise<void> {
    if (this.busy()) return;

    await this.#whileBusy("removing…", async () => {
      const answer = await this.#connection.removeAssistantSettings();
      if (!answer) {
        this.note.set("Could not reach the server — try again.");
        return;
      }

      this.#accept(answer);
      this.note.set("Removed — the dictionaries answer again.");
    });
  }

  #accept(settings: AssistantSettingsView): void {
    this.#state.assistant.set(settings);
  }

  async #whileBusy(label: string, work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.note.set(label);
    try {
      await work();
    } finally {
      this.busy.set(false);
    }
  }

  #refresh(): void {
    const settings = this.settings();
    this.service.set(settings.serviceId ?? settings.services[0]?.id ?? "");
    this.model().nativeElement.value = settings.model ?? "";
    this.key().nativeElement.value = "";
  }
}
