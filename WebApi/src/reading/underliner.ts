/**
 * Keeps already-rendered paragraphs in step with the vocabulary.
 *
 * The server marks up every paragraph it sends (D3), so this only has to handle text that
 * is already on screen when a word is saved or deleted — it is a live patch, not the
 * primary mechanism.
 */

const knownWordClass = "known-word";

/** Escapes a term for use inside a regular expression. */
function escapeForRegExp(term: string): string {
  return term.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function buildMatcher(normalizedTerm: string): RegExp {
  // Whitespace in a saved phrase may be any run of whitespace in the text.
  const pattern = escapeForRegExp(normalizedTerm).replace(/\\?\s+/g, "\\s+");
  return new RegExp(`\\b${pattern}\\b`, "gi");
}

/** Text nodes under `root` that are not already inside a marked word. */
function collectTextNodes(root: HTMLElement): Text[] {
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
    acceptNode(node: Node): number {
      const parent = node.parentElement;
      if (!parent) return NodeFilter.FILTER_REJECT;
      if (parent.classList.contains(knownWordClass)) return NodeFilter.FILTER_REJECT;
      return node.textContent && node.textContent.trim().length > 0
        ? NodeFilter.FILTER_ACCEPT
        : NodeFilter.FILTER_REJECT;
    },
  });

  const nodes: Text[] = [];
  let current = walker.nextNode();
  while (current) {
    if (current instanceof Text) nodes.push(current);
    current = walker.nextNode();
  }

  return nodes;
}

/** Wraps every occurrence of a newly saved term in the text already on screen. */
export function underlineTerm(root: HTMLElement, normalizedTerm: string): void {
  if (normalizedTerm.length === 0) return;

  const matcher = buildMatcher(normalizedTerm);

  for (const textNode of collectTextNodes(root)) {
    const text = textNode.textContent ?? "";
    matcher.lastIndex = 0;
    if (!matcher.test(text)) continue;

    matcher.lastIndex = 0;
    const fragment = document.createDocumentFragment();
    let lastIndex = 0;

    for (const match of text.matchAll(matcher)) {
      const start = match.index;
      if (start === undefined) continue;

      if (start > lastIndex) {
        fragment.appendChild(document.createTextNode(text.slice(lastIndex, start)));
      }

      const marked = document.createElement("span");
      marked.className = knownWordClass;
      marked.dataset["term"] = normalizedTerm;
      marked.textContent = match[0];
      fragment.appendChild(marked);

      lastIndex = start + match[0].length;
    }

    if (lastIndex < text.length) {
      fragment.appendChild(document.createTextNode(text.slice(lastIndex)));
    }

    textNode.parentNode?.replaceChild(fragment, textNode);
  }
}

/** Unwraps a term that has just been deleted from the vocabulary. */
export function removeUnderline(root: HTMLElement, normalizedTerm: string): void {
  const marked = root.querySelectorAll<HTMLElement>(`.${knownWordClass}`);

  for (const element of Array.from(marked)) {
    if (element.dataset["term"] !== normalizedTerm) continue;

    const parent = element.parentNode;
    if (!parent) continue;

    parent.replaceChild(document.createTextNode(element.textContent ?? ""), element);
    parent.normalize();
  }
}
