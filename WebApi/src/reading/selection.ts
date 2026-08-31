/**
 * Reading the word or phrase the user has highlighted, and finding where on screen it sits
 * so the definition box can point at it.
 */

export interface SelectedTerm {
  readonly text: string;
  /** Viewport coordinates of the selection, for positioning the box. */
  readonly rect: DOMRect;
}

/** Longest phrase worth looking up; matches the server's phrase cap (D4). */
const maxWords = 4;

export function readSelection(): SelectedTerm | undefined {
  const selection = window.getSelection();
  if (!selection || selection.isCollapsed || selection.rangeCount === 0) return undefined;

  const text = selection.toString().trim().replace(/\s+/g, " ");
  if (text.length === 0) return undefined;
  if (text.split(" ").length > maxWords) return undefined;

  const range = selection.getRangeAt(0);
  const rect = range.getBoundingClientRect();
  if (rect.width === 0 && rect.height === 0) return undefined;

  return { text, rect };
}

/** The rect of an already-underlined word, used when one is clicked. */
export function rectOf(element: Element): DOMRect {
  return element.getBoundingClientRect();
}

export function clearSelection(): void {
  window.getSelection()?.removeAllRanges();
}
