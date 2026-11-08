/*
 * The reader's service worker (D51): what lets the installed app open with no connection.
 *
 * It keeps three things, and touches nothing else — the hub, the auth calls and every POST go
 * straight to the network:
 *
 *  - the reading page's shell, network first: a reader online always gets the current build
 *    (and the server's sign-in check), a reader offline gets the last shell that loaded;
 *  - the build's files, cache first: their names carry a hash of their content, so a cached
 *    one is never stale. The page tells the worker which files it is running on, so they are
 *    kept even when they loaded before the worker was in control, and older builds' are dropped;
 *  - the pictures of downloaded chapters, cache first. The page puts them there when it
 *    downloads a chapter; the worker never adds to that cache, so it only ever holds what the
 *    reader chose to keep.
 *
 * Plain JavaScript in `public/`, outside the Angular build, so its URL never changes — a worker
 * is found again by its URL. Served `no-cache` like everything unhashed (ClientAssets).
 */

const SHELL_CACHE = "novelreader-shell-v1";
const ASSET_CACHE = "novelreader-assets-v1";
/** Shared with the page (core/offline-store.ts), which fills it. */
const IMAGE_CACHE = "novelreader-book-images";
const KEPT_CACHES = [SHELL_CACHE, ASSET_CACHE, IMAGE_CACHE];

const scope = self.registration.scope;
const shellUrl = new URL("ReadingPage", scope).href;

/** Content-hashed build output, the same pattern the server caches for a year (ClientAssets). */
const hashedFile = /-[A-Za-z0-9_-]{8}\.(js|css|ttf|woff2?)$/;
/** Unhashed but static: the icons and the manifest. */
const staticFile = /\/(icons\/[^/]+\.png|manifest\.webmanifest)$/;
const bookImage = /\/books\/book--[0-9a-f]{16}\/images\/[0-9a-f]{64}\.(jpg|png|gif|webp)$/;

self.addEventListener("install", (event) => {
  // The reader registered from the reading page, signed in, so the shell is theirs to keep.
  event.waitUntil(storeShell(fetch(shellUrl, { credentials: "same-origin", redirect: "manual" })).catch(() => undefined));
  self.skipWaiting();
});

self.addEventListener("activate", (event) => {
  event.waitUntil((async () => {
    for (const name of await caches.keys()) {
      if (name.startsWith("novelreader-") && !KEPT_CACHES.includes(name)) await caches.delete(name);
    }
    await self.clients.claim();
  })());
});

/**
 * `{ type: "keep-assets", urls }`: the files the page is running on. Fetched into the cache if
 * they are not there, and any other build's hashed files dropped, so the cache holds one build.
 */
self.addEventListener("message", (event) => {
  const data = event.data;
  if (!data || data.type !== "keep-assets" || !Array.isArray(data.urls)) return;

  event.waitUntil((async () => {
    const cache = await caches.open(ASSET_CACHE);
    const wanted = new Set(data.urls.filter((url) => typeof url === "string" && url.startsWith(scope)));

    for (const url of wanted) {
      if (!(await cache.match(url))) {
        try {
          const response = await fetch(url, { credentials: "same-origin" });
          if (response.ok) await cache.put(url, response);
        } catch {
          // Offline right now; it is cached on the next load instead.
        }
      }
    }

    for (const request of await cache.keys()) {
      if (hashedFile.test(new URL(request.url).pathname) && !wanted.has(request.url)) await cache.delete(request);
    }
  })());
});

self.addEventListener("fetch", (event) => {
  const request = event.request;
  if (request.method !== "GET" || !request.url.startsWith(scope)) return;

  const path = new URL(request.url).pathname;

  if (request.mode === "navigate") {
    if (request.url.split(/[?#]/)[0] === shellUrl) event.respondWith(networkFirstShell(request));
    return;
  }

  if (bookImage.test(path)) {
    event.respondWith(fromCacheElseNetwork(IMAGE_CACHE, request, false));
  } else if (hashedFile.test(path) || staticFile.test(path)) {
    event.respondWith(fromCacheElseNetwork(ASSET_CACHE, request, true));
  }
});

async function networkFirstShell(request) {
  try {
    return await storeShell(fetch(request));
  } catch {
    const cached = await caches.match(shellUrl, { cacheName: SHELL_CACHE });
    return cached ?? Response.error();
  }
}

/**
 * Keeps the shell only when it really is the reading page: a reader signed out is redirected
 * to the login screen, and that must not be what opens offline.
 */
async function storeShell(responsePromise) {
  const response = await responsePromise;
  if (response.ok && response.type === "basic" && !response.redirected) {
    const cache = await caches.open(SHELL_CACHE);
    await cache.put(shellUrl, response.clone());
  }
  return response;
}

async function fromCacheElseNetwork(cacheName, request, fill) {
  const cache = await caches.open(cacheName);
  const cached = await cache.match(request);
  if (cached) return cached;

  const response = await fetch(request);
  if (fill && response.ok) await cache.put(request, response.clone());
  return response;
}
