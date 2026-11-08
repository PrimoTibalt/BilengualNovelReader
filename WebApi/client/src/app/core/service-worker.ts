import { appUrl } from "./app-base";

/**
 * Registers the service worker that lets the installed app open offline (D51), and tells it
 * which of the build's files this page is running on so it keeps exactly those — including the
 * ones that loaded before it was in control, which it never saw go past.
 *
 * Registered under the app's base, so behind nginx its scope is `/reader/` and nothing else on
 * the domain is touched.
 */
export function registerServiceWorker(): void {
  if (!("serviceWorker" in navigator)) return;

  const register = async (): Promise<void> => {
    try {
      const registration = await navigator.serviceWorker.register(appUrl("sw.js"));
      const worker = registration.active ?? (await navigator.serviceWorker.ready).active;
      worker?.postMessage({ type: "keep-assets", urls: loadedBuildFiles() });
    } catch (error: unknown) {
      // No worker is no offline start; the page itself works the same.
      console.warn("Service worker not registered:", error);
    }
  };

  // After the page is up, so the lazily loaded page code and the font are in the list.
  if (document.readyState === "complete") setTimeout(() => void register(), 1000);
  else window.addEventListener("load", () => setTimeout(() => void register(), 1000), { once: true });
}

/** The scripts, styles and fonts this page loaded from the app itself. */
function loadedBuildFiles(): string[] {
  const base = appUrl("");
  const files = performance
    .getEntriesByType("resource")
    .map((entry) => entry.name.split(/[?#]/)[0]!)
    .filter((url) => url.startsWith(base) && /\.(js|css|ttf|woff2?)$/.test(url));

  return [...new Set(files)];
}
