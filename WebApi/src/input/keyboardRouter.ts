/**
 * Modal keyboard dispatch, in the spirit of a TUI: a stack of named modes, the innermost
 * consulted first. Opening the definition box pushes a mode; closing it pops one, and the
 * bindings underneath come back untouched. See D6 in DECISIONS.md.
 */

export type KeyHandler = (event: KeyboardEvent) => void;

export interface KeyBinding {
  /** Shown in the hint bar. Keep it to a word or two. */
  readonly description: string;
  readonly run: KeyHandler;
}

export interface KeyMode {
  readonly name: string;
  readonly bindings: ReadonlyMap<string, KeyBinding>;
  /** Called when this mode becomes the innermost one. */
  readonly onEnter?: () => void;
  /** Called when it stops being the innermost one, including when it is popped. */
  readonly onExit?: () => void;
}

/** Elements that own their keystrokes; the router keeps its hands off them. */
function isTextEntry(element: Element | null): boolean {
  if (!(element instanceof HTMLElement)) return false;
  if (element.isContentEditable) return true;

  const tag = element.tagName;
  return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT";
}

export class KeyboardRouter {
  readonly #modes: KeyMode[] = [];
  #listening = false;

  /** The mode currently receiving keys, if any. */
  get activeMode(): KeyMode | undefined {
    return this.#modes[this.#modes.length - 1];
  }

  get activeModeName(): string | undefined {
    return this.activeMode?.name;
  }

  start(): void {
    if (this.#listening) return;
    document.addEventListener("keydown", this.#handleKeyDown, { capture: false });
    this.#listening = true;
  }

  stop(): void {
    if (!this.#listening) return;
    document.removeEventListener("keydown", this.#handleKeyDown);
    this.#listening = false;
  }

  push(mode: KeyMode): void {
    this.activeMode?.onExit?.();
    this.#modes.push(mode);
    mode.onEnter?.();
  }

  /** Pops the named mode if it is on top. Named so a stale close cannot pop the wrong one. */
  pop(name: string): void {
    const top = this.activeMode;
    if (!top || top.name !== name) return;

    this.#modes.pop();
    top.onExit?.();
    this.activeMode?.onEnter?.();
  }

  has(name: string): boolean {
    return this.#modes.some((mode) => mode.name === name);
  }

  #handleKeyDown = (event: KeyboardEvent): void => {
    // The navigation menu and any text field handle their own keys.
    if (event.defaultPrevented) return;
    if (isTextEntry(document.activeElement)) return;
    if (event.ctrlKey || event.metaKey || event.altKey) return;

    const binding = this.activeMode?.bindings.get(event.key);
    if (!binding) return;

    event.preventDefault();
    binding.run(event);
  };
}

/** Convenience for building a mode's binding table readably. */
export function bindings(entries: Record<string, KeyBinding>): ReadonlyMap<string, KeyBinding> {
  return new Map(Object.entries(entries));
}
