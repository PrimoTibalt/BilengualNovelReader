import { Injectable } from "@angular/core";
import type { BookContentsView, ChapterWire, ParagraphView } from "./hub-models";

/**
 * Chapters kept on the device for reading offline (D51), in IndexedDB, with their pictures in
 * the Cache Storage the service worker serves them from.
 *
 * What is stored is what the hub answered — the chapter's markup, already escaped by the server
 * (D3) — so a stored chapter renders exactly as a loaded one. Saved words are underlined as they
 * were when it was downloaded; a chapter read online is stored again as it arrives, so the copy
 * catches up whenever it is read with a connection.
 *
 * Everything is kept per reader: two people signed in on one phone keep separate shelves. The
 * reader is known from the session online, and from the last one remembered when the app opens
 * offline.
 *
 * Besides chapters it keeps a bookmark per novel. Read offline, the bookmark cannot reach the
 * server; it is kept here marked unsent and sent once the connection is back.
 */

/** Shared with the service worker (public/sw.js), which serves pictures from it. */
const imageCacheName = "novelreader-book-images";
const lastReaderKey = "novelreader.offline.reader";
const databaseName = "novelreader-offline";
const databaseVersion = 1;

/** Book pictures in a chapter's markup, as the server writes them (ParagraphMarkupBuilder). */
const imageSource = /src="(books\/book--[0-9a-f]{16}\/images\/[0-9a-f]{64}\.(?:jpg|png|gif|webp))"/g;

export interface StoredChapter {
  readonly user: string;
  readonly novel: string;
  readonly chapter: number;
  readonly paragraphs: readonly ParagraphView[];
  readonly savedAt: number;
}

export interface StoredNovel {
  readonly user: string;
  readonly slug: string;
  readonly title: string | null;
  readonly totalChapters: number | null;
  /** An uploaded book's table of contents, so its chapter menu has names offline too. */
  readonly contents: BookContentsView | null;
}

export interface StoredPosition {
  readonly user: string;
  readonly novel: string;
  readonly chapter: number;
  readonly paragraph: number;
  readonly updatedAt: number;
  /** False until the server has it: read offline, or sent and not yet answered. */
  readonly sent: boolean;
}

/** A request as a promise. */
function done<T>(request: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

function committed(transaction: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(transaction.error);
    transaction.onabort = () => reject(transaction.error);
  });
}

@Injectable({ providedIn: "root" })
export class OfflineStore {
  #database: Promise<IDBDatabase> | undefined;
  #user: string | null = null;

  constructor() {
    try {
      this.#user = localStorage.getItem(lastReaderKey);
    } catch {
      // Storage blocked: the reader is learnt from the session instead.
    }
  }

  /** The reader the shelf belongs to: the last one signed in on this device, until the session says. */
  get user(): string | null {
    return this.#user;
  }

  /** Called with the session's reader; remembered for the next time the app opens offline. */
  useReader(userName: string): void {
    this.#user = userName;
    try {
      localStorage.setItem(lastReaderKey, userName);
    } catch {
      // Blocked: only an offline start loses out.
    }
  }

  // ---- Chapters ----

  async getChapter(novel: string, chapter: number): Promise<ChapterWire | undefined> {
    const user = this.#user;
    if (user === null) return undefined;

    const stored = await this.#read<StoredChapter | undefined>("chapters", (store) => store.get([user, novel, chapter]));
    return stored && { novelName: novel, chapterNumber: chapter, paragraphs: stored.paragraphs, found: true, failure: null };
  }

  async hasChapter(novel: string, chapter: number): Promise<boolean> {
    const user = this.#user;
    if (user === null) return false;
    return (await this.#read<number>("chapters", (store) => store.count([user, novel, chapter]))) > 0;
  }

  /** The numbers of this novel's chapters on the device, in order. */
  async chapterNumbers(novel: string): Promise<number[]> {
    const user = this.#user;
    if (user === null) return [];

    const keys = await this.#read<IDBValidKey[]>("chapters", (store) =>
      store.getAllKeys(IDBKeyRange.bound([user, novel, 0], [user, novel, Number.MAX_SAFE_INTEGER])));
    return keys.map((key) => (key as [string, string, number])[2]).sort((left, right) => left - right);
  }

  /**
   * Keeps a chapter, pictures first: a chapter is only on the shelf once everything it shows is.
   * Throws when a picture could not be fetched, so the download can say the chapter failed.
   */
  async putChapter(chapter: ChapterWire): Promise<void> {
    const user = this.#user;
    if (user === null || !chapter.found) return;

    await this.#keepImages(chapter.paragraphs);

    const stored: StoredChapter = {
      user,
      novel: chapter.novelName,
      chapter: chapter.chapterNumber,
      paragraphs: chapter.paragraphs,
      savedAt: Date.now(),
    };
    await this.#write("chapters", (store) => store.put(stored));
  }

  /** A chapter just read online replaces the stored copy, so its underlines stay current. */
  async refreshIfStored(chapter: ChapterWire): Promise<void> {
    if (chapter.found && (await this.hasChapter(chapter.novelName, chapter.chapterNumber))) {
      await this.putChapter(chapter);
    }
  }

  /** Forgets a novel's chapters, and a book's pictures with them. */
  async removeNovel(novel: string): Promise<void> {
    const user = this.#user;
    if (user === null) return;

    await this.#write("chapters", (store) =>
      store.delete(IDBKeyRange.bound([user, novel, 0], [user, novel, Number.MAX_SAFE_INTEGER])));

    if ("caches" in window && novel.startsWith("book--")) {
      const cache = await caches.open(imageCacheName);
      const prefix = new URL(`books/${novel}/`, document.baseURI).href;
      for (const request of await cache.keys()) {
        if (request.url.startsWith(prefix)) await cache.delete(request);
      }
    }
  }

  async #keepImages(paragraphs: readonly ParagraphView[]): Promise<void> {
    const sources = new Set<string>();
    for (const paragraph of paragraphs) {
      for (const match of paragraph.markup.matchAll(imageSource)) sources.add(new URL(match[1]!, document.baseURI).href);
    }
    if (sources.size === 0 || !("caches" in window)) return;

    const cache = await caches.open(imageCacheName);
    for (const source of sources) {
      if (await cache.match(source)) continue;

      const response = await fetch(source, { credentials: "same-origin" });
      if (!response.ok) throw new Error(`Picture ${source} answered ${response.status}.`);
      await cache.put(source, response);
    }
  }

  // ---- Novels ----

  async putNovel(novel: Omit<StoredNovel, "user">): Promise<void> {
    const user = this.#user;
    if (user === null) return;
    await this.#write("novels", (store) => store.put({ ...novel, user }));
  }

  async getNovel(slug: string): Promise<StoredNovel | undefined> {
    const user = this.#user;
    if (user === null) return undefined;
    return await this.#read<StoredNovel | undefined>("novels", (store) => store.get([user, slug]));
  }

  async novels(): Promise<StoredNovel[]> {
    const user = this.#user;
    if (user === null) return [];
    return await this.#read<StoredNovel[]>("novels", (store) =>
      store.getAll(IDBKeyRange.bound([user, ""], [user, "￿"])));
  }

  // ---- Bookmarks ----

  async savePosition(novel: string, chapter: number, paragraph: number, sent: boolean): Promise<void> {
    const user = this.#user;
    if (user === null) return;

    const position: StoredPosition = { user, novel, chapter, paragraph, updatedAt: Date.now(), sent };
    await this.#write("positions", (store) => store.put(position));
  }

  /** Marks a bookmark sent, unless the reader has moved on since it was. */
  async markSent(novel: string, chapter: number, paragraph: number): Promise<void> {
    const user = this.#user;
    if (user === null) return;

    const database = await this.#open();
    const transaction = database.transaction("positions", "readwrite");
    const store = transaction.objectStore("positions");
    const current = await done<StoredPosition | undefined>(store.get([user, novel]));
    if (current && current.chapter === chapter && current.paragraph === paragraph) {
      store.put({ ...current, sent: true });
    }
    await committed(transaction);
  }

  async positions(): Promise<StoredPosition[]> {
    const user = this.#user;
    if (user === null) return [];
    const all = await this.#read<StoredPosition[]>("positions", (store) =>
      store.getAll(IDBKeyRange.bound([user, ""], [user, "￿"])));
    return all.sort((left, right) => left.updatedAt - right.updatedAt);
  }

  // ---- The device ----

  /**
   * Asks the browser not to clear the shelf when the phone runs short of space. Granted to an
   * installed app; refused or unsupported, the chapters are kept the ordinary, evictable way.
   */
  async persist(): Promise<void> {
    try {
      if (navigator.storage?.persisted && !(await navigator.storage.persisted())) await navigator.storage.persist();
    } catch {
      // Not offered here.
    }
  }

  async usedBytes(): Promise<number | undefined> {
    try {
      return (await navigator.storage?.estimate())?.usage;
    } catch {
      return undefined;
    }
  }

  // ---- IndexedDB ----

  #open(): Promise<IDBDatabase> {
    this.#database ??= new Promise<IDBDatabase>((resolve, reject) => {
      const request = indexedDB.open(databaseName, databaseVersion);
      request.onupgradeneeded = () => {
        const database = request.result;
        database.createObjectStore("chapters", { keyPath: ["user", "novel", "chapter"] });
        database.createObjectStore("novels", { keyPath: ["user", "slug"] });
        database.createObjectStore("positions", { keyPath: ["user", "novel"] });
      };
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    }).catch((error: unknown) => {
      // Asked again next time rather than failing for good: a private window may allow it later.
      this.#database = undefined;
      throw error;
    });

    return this.#database;
  }

  async #read<T>(storeName: string, query: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
    const database = await this.#open();
    return await done(query(database.transaction(storeName, "readonly").objectStore(storeName)));
  }

  async #write(storeName: string, change: (store: IDBObjectStore) => IDBRequest): Promise<void> {
    const database = await this.#open();
    const transaction = database.transaction(storeName, "readwrite");
    change(transaction.objectStore(storeName));
    await committed(transaction);
  }
}
