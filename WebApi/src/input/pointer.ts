/**
 * Whether the primary input is touch — a phone or tablet with no hover and a coarse
 * pointer. This, not the screen width, is the signal for "the keyboard-only controls need
 * tappable equivalents": a narrow window on a laptop still has a keyboard, and a large
 * tablet does not.
 *
 * Read once at load. A device does not grow a keyboard mid-session often enough to justify
 * watching the query, and the reading page builds its chrome once.
 */
export const isTouchPrimary: boolean =
  typeof window.matchMedia === "function" &&
  window.matchMedia("(hover: none) and (pointer: coarse)").matches;

/**
 * Stamps the touch state onto the document element so the stylesheets can switch the chrome
 * — the tappable menu button, the definition-box buttons, the larger hit targets — from CSS
 * rather than from script.
 */
export function markPointerMode(): void {
  document.documentElement.classList.toggle("touch-input", isTouchPrimary);
}
