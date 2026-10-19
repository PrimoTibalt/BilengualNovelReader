/**
 * Reading a chapter number out of what someone typed into the chapter filter.
 *
 * Deliberately strict. `Number` alone accepts "1e3", " 12 ", "0x10" and "12.0", none of which
 * anyone means as a chapter number, and offering to jump to chapter 1000 because the reader
 * typed "1e3" would be worse than offering nothing.
 */
export function parseChapterNumber(text: string): number | undefined {
  const trimmed = text.trim();
  if (!/^[0-9]+$/.test(trimmed)) return undefined;

  const value = Number(trimmed);
  return Number.isSafeInteger(value) && value >= 1 ? value : undefined;
}
