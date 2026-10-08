import type { AssistantSettingsView } from "../reading/connection.js";
import { settingsActions, settingsButton, settingsNote, settingsRow, type SettingsSection } from "./settingsPanel.js";

export interface AssistantSectionCallbacks {
  readonly onSave: (serviceId: string, model: string, apiKey: string) => Promise<AssistantSettingsView | undefined>;
  readonly onRemove: () => Promise<AssistantSettingsView | undefined>;
  /** Told whenever the stored settings change, so the page knows whether to send context. */
  readonly onChanged: (settings: AssistantSettingsView) => void;
}

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
export class AssistantSection implements SettingsSection {
  readonly title = "ai definitions & translations";

  readonly #callbacks: AssistantSectionCallbacks;
  #settings: AssistantSettingsView;
  #service: HTMLSelectElement | undefined;
  #model: HTMLInputElement | undefined;
  #key: HTMLInputElement | undefined;
  #status: HTMLElement | undefined;
  #note: HTMLElement | undefined;
  #save: HTMLButtonElement | undefined;
  #remove: HTMLButtonElement | undefined;
  #busy = false;

  constructor(settings: AssistantSettingsView, callbacks: AssistantSectionCallbacks) {
    this.#settings = settings;
    this.#callbacks = callbacks;
  }

  /** The page learns the settings after the panel is built — from the reading session. */
  setSettings(settings: AssistantSettingsView): void {
    this.#settings = settings;
    this.refresh();
  }

  build(container: HTMLElement): void {
    const status = document.createElement("p");
    status.className = "settings-status";
    container.appendChild(status);
    this.#status = status;

    const service = document.createElement("select");
    service.className = "settings-field";
    service.setAttribute("aria-label", "AI service");
    service.addEventListener("change", () => this.#renderModelPlaceholder());
    container.appendChild(settingsRow("service", service));
    this.#service = service;

    const model = document.createElement("input");
    model.type = "text";
    model.className = "settings-field";
    model.spellcheck = false;
    model.autocomplete = "off";
    model.setAttribute("autocapitalize", "off");
    model.setAttribute("aria-label", "Model");
    container.appendChild(settingsRow("model", model));
    this.#model = model;

    const key = document.createElement("input");
    key.type = "password";
    key.className = "settings-field";
    key.spellcheck = false;
    // Not a site password: stop the browser offering to save it as one.
    key.autocomplete = "off";
    key.setAttribute("autocapitalize", "off");
    key.setAttribute("aria-label", "API key");
    container.appendChild(settingsRow("api key", key));
    this.#key = key;

    const note = settingsNote();
    container.appendChild(note);
    this.#note = note;

    this.#save = settingsButton("save", () => void this.#submit());
    this.#remove = settingsButton("remove", () => void this.#forget());
    container.appendChild(settingsActions(this.#save, this.#remove));

    this.refresh();
  }

  refresh(): void {
    const settings = this.#settings;
    const service = this.#service;
    if (!service || !this.#model || !this.#key) return;

    service.replaceChildren();
    for (const offered of settings.services) {
      const option = document.createElement("option");
      option.value = offered.id;
      option.textContent = offered.name;
      service.appendChild(option);
    }
    service.value = settings.serviceId ?? settings.services[0]?.id ?? "";

    this.#model.value = settings.model ?? "";
    this.#key.value = "";
    this.#renderModelPlaceholder();
    this.#renderStatus();
    this.#showNote("");
  }

  async #submit(): Promise<void> {
    if (this.#busy || !this.#service || !this.#model || !this.#key) return;

    const serviceId = this.#service.value;
    const model = this.#model.value.trim();
    const key = this.#key.value.trim();

    // The server checks all of this again; these only save a round trip for the obvious.
    if (model.length === 0) {
      this.#showNote("Enter the model to use.");
      this.#model.focus();
      return;
    }
    if (key.length === 0 && this.#settings.keyHint === null) {
      this.#showNote("Paste the API key for this service.");
      this.#key.focus();
      return;
    }

    await this.#whileBusy("saving…", async () => {
      const answer = await this.#callbacks.onSave(serviceId, model, key);
      if (!answer) {
        this.#showNote("Could not reach the server — try again.");
        return;
      }

      if (answer.error !== null) {
        this.#showNote(describeError(answer.error));
        return;
      }

      this.#accept(answer);
      this.#showNote("Saved.");
    });
  }

  async #forget(): Promise<void> {
    if (this.#busy) return;

    await this.#whileBusy("removing…", async () => {
      const answer = await this.#callbacks.onRemove();
      if (!answer) {
        this.#showNote("Could not reach the server — try again.");
        return;
      }

      this.#accept(answer);
      this.#showNote("Removed — the dictionaries answer again.");
    });
  }

  #accept(settings: AssistantSettingsView): void {
    this.#settings = settings;
    this.#callbacks.onChanged(settings);
    this.refresh();
  }

  async #whileBusy(label: string, work: () => Promise<void>): Promise<void> {
    this.#busy = true;
    this.#showNote(label);
    if (this.#save) this.#save.disabled = true;
    if (this.#remove) this.#remove.disabled = true;

    try {
      await work();
    } finally {
      this.#busy = false;
      if (this.#save) this.#save.disabled = false;
      this.#renderStatus();
    }
  }

  #renderStatus(): void {
    const settings = this.#settings;
    const configured = settings.serviceId !== null;

    if (this.#status) {
      const serviceName = settings.services.find((service) => service.id === settings.serviceId)?.name
        ?? settings.serviceId;
      this.#status.textContent = configured
        ? `on — ${settings.model} via ${serviceName}, key …${settings.keyHint}`
        : "off — the dictionaries and MyMemory answer.";
      this.#status.classList.toggle("settings-status--on", configured);
    }

    if (this.#key) {
      this.#key.placeholder = configured ? "empty keeps saved key" : "paste your key";
    }

    if (this.#remove) this.#remove.disabled = this.#busy || !configured;
  }

  #renderModelPlaceholder(): void {
    if (!this.#model || !this.#service) return;

    const example = exampleModels[this.#service.value];
    this.#model.placeholder = example ? `e.g. ${example}` : "model id";
  }

  #showNote(text: string): void {
    if (this.#note) this.#note.textContent = text;
  }
}
