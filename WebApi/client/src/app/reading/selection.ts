/**
 * Reading the word or phrase the user has highlighted, and finding where on screen it sits
 * so the definition box can point at it.
 */

export interface SelectedTerm {
  readonly text: string;
  /** Viewport coordinates of the selection, for positioning the box. */
  readonly rect: DOMRect;
  /**
   * A detached copy of the selected range, so a selection captured now can still be measured
   * later — after the page has scrolled under it, or after the browser dropped the live
   * selection because something else was tapped (D26).
   */
  readonly range: Range;
  /**
   * How many words were selected. Translation is offered only for more than one: a single
   * word already has a definition, and its translation sits inside that box (D32).
   */
  readonly wordCount: number;
}

/** Longest phrase worth looking up; matches the server's phrase cap (D4). */
const maxWords = 4;

export function readSelection(): SelectedTerm | undefined {
  const selection = window.getSelection();
  if (!selection || selection.isCollapsed || selection.rangeCount === 0) return undefined;

  const text = selection.toString().trim().replace(/\s+/g, " ");
  if (text.length === 0) return undefined;

  const wordCount = text.split(" ").length;
  if (wordCount > maxWords) return undefined;

  const range = selection.getRangeAt(0);
  const rect = range.getBoundingClientRect();
  if (rect.width === 0 && rect.height === 0) return undefined;

  return { text, rect, range: range.cloneRange(), wordCount };
}

/**
 * Where a captured range sits on screen *now*. Undefined once the range measures nothing —
 * its text was replaced, so the selection it stood for is gone.
 */
export function rectOfRange(range: Range): DOMRect | undefined {
  const rect = range.getBoundingClientRect();
  if (rect.width === 0 && rect.height === 0) return undefined;

  return rect;
}

/** The rect of an already-underlined word, used when one is clicked. */
export function rectOf(element: Element): DOMRect {
  return element.getBoundingClientRect();
}

export function clearSelection(): void {
  window.getSelection()?.removeAllRanges();
}
