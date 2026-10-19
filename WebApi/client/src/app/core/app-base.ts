/**
 * A URL under the path the app is mounted at — `/reader/` behind nginx, `/` locally. The server
 * writes that path into the page's `<base href>`, so everything the page calls resolves against
 * it rather than against the site root.
 */
export function appUrl(path: string): string {
  return new URL(path, document.baseURI).toString();
}
