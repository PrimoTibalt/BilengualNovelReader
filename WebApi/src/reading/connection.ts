import type * as SignalR from "@microsoft/signalr";
import type { DefinitionView, TranslationView } from "../ui/definitionBox.js";

/**
 * The SignalR client is loaded as a classic script in index.html, so it is a global. The
 * type-only import above is erased at compile time and merely types that global.
 */
declare const signalR: typeof SignalR;

/**
 * No call carries a user name. The server takes the reader from the authentication cookie,
 * so the page cannot name itself (D20).
 */

/** Wire shapes, camelCased by SignalR's JSON protocol. */
interface DefinitionWire {
  readonly term: string;
  readonly surfaceForm: string;
  readonly senses: readonly {
    readonly partOfSpeech: string | null;
    readonly text: string;
    readonly example: string | null;
  }[];
  readonly sourceName: string | null;
  readonly sourceUrl: string | null;
  readonly isSaved: boolean;
  readonly found: boolean;
}

interface TranslationWire {
  readonly term: string;
  /** What was selected, echoed back — this is what an answer is matched against (D32). */
  readonly surfaceForm: string;
  readonly text: string | null;
  readonly targetLanguage: string | null;
  /** A code, not a sentence: `not-configured` opens the settings form (D31). */
  readonly error: string | null;
}

interface TranslationSettingsWire {
  readonly email: string | null;
  readonly language: string | null;
  readonly error: string | null;
}

/** One language the settings form offers, as the server defines it. */
export interface TranslationLanguageView {
  readonly code: string;
  readonly name: string;
}

/** The answer to a settings save: stored, or refused with the field that was wrong. */
export interface TranslationSettingsResult {
  readonly email: string | null;
  readonly language: string | null;
  readonly error: string | null;
}

interface VocabularyChangedWire {
  readonly term: string;
  readonly isSaved: boolean;
}

export interface ParagraphView {
  readonly number: number;
  readonly markup: string;
}

/** A chapter as the hub answers it. */
interface ChapterWire {
  readonly novelName: string;
  readonly chapterNumber: number;
  readonly paragraphs: readonly ParagraphView[];
  readonly found: boolean;
}

export interface ChapterView extends ChapterWire {
  /**
   * True when the *call* did not get through, as opposed to the server saying there is no
   * such chapter. The difference matters: an empty answer is the end of the novel, a failed
   * call is a connection to wait out and retry (D28).
   */
  readonly failed: boolean;
}

/** One hit from a novel search. `slug` is the name every other call uses. */
export interface NovelSearchResult {
  readonly title: string;
  readonly slug: string;
  readonly rank: number | null;
  readonly totalChapters: number | null;
}

/**
 * A novel in the reader's library. Everything but the slug comes from the catalogue and is
 * refreshed daily, so any of it may be missing for a novel not looked up yet (D22).
 */
export interface NovelSummary {
  readonly slug: string;
  readonly title: string | null;
  readonly rank: number | null;
  readonly totalChapters: number | null;
}

/** The first answer the page gets: who the reader is, their novels, and where they stopped. */
export interface ReadingSessionView {
  readonly userName: string;
  readonly novels: readonly NovelSummary[];
  readonly novelName: string;
  readonly chapterNumber: number;
  readonly paragraphNumber: number;
  readonly resuming: boolean;
  /** Both null until the reader has set translation up; that is what makes `t` ask first. */
  readonly translationEmail: string | null;
  readonly translationLanguage: string | null;
  readonly translationLanguages: readonly TranslationLanguageView[];
}

/**
 * What the page shows about the link to the server: connected, or trying to get back. There
 * is no third state — the client never stops trying (D28).
 */
export type ConnectionState = "connected" | "reconnecting";

export interface ReaderConnectionCallbacks {
  onDefinition(view: DefinitionView): void;
  /** Keyed by the surface form that was asked for, not the term it normalised to (D32). */
  onTranslation(surfaceForm: string, translation: TranslationView): void;
  onVocabularyChanged(term: string, isSaved: boolean): void;
  onConnectionStateChanged(state: ConnectionState): void;
}

/** How long to wait between attempts to get the connection back. */
const reconnectDelayInMilliseconds = 1000;

/**
 * How long a hub call waits for a dropped connection before giving up on this attempt. The
 * reader is not told to reload: the caller retries once the connection returns.
 */
const connectionWaitInMilliseconds = 30_000;

const delay = (milliseconds: number): Promise<void> =>
  new Promise((resolve) => setTimeout(resolve, milliseconds));

/**
 * A 401 from the hub means the auth cookie is gone, not that the network is down — retrying
 * would loop forever on a session that is over. The page goes back to the login screen.
 */
function isSessionExpired(error: unknown): boolean {
  return error instanceof signalR.HttpError && error.statusCode === 401;
}

export class ReaderConnection {
  readonly #connection: SignalR.HubConnection;
  readonly #callbacks: ReaderConnectionCallbacks;
  /** Calls parked until the connection is back; resolved by {@link #setState}. */
  readonly #waiters = new Set<() => void>();
  /** Unset until the first attempt settles, so that first answer always reaches the page. */
  #state: ConnectionState | undefined;
  /** Guards against two connect loops running at once. */
  #connecting = false;

  constructor(callbacks: ReaderConnectionCallbacks) {
    this.#callbacks = callbacks;

    this.#connection = new signalR.HubConnectionBuilder()
      .withUrl("/signalr")
      // A second apart, for as long as it takes: a reader who put their phone down mid-
      // chapter comes back to a page that has quietly reconnected (D28).
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (context) =>
          isSessionExpired(context.retryReason) ? null : reconnectDelayInMilliseconds,
      })
      .build();

    this.#connection.onreconnecting(() => this.#setState("reconnecting"));
    this.#connection.onreconnected(() => this.#setState("connected"));

    // Reached only when the retry policy above gave up — an expired session — or when the
    // hub closed the connection outright. Either way the loop below decides what happens.
    this.#connection.onclose((error) => {
      this.#setState("reconnecting");
      void this.#connectWithRetry(error);
    });

    this.#connection.on("ReturnDefinition", (payload: DefinitionWire) => {
      callbacks.onDefinition({
        term: payload.term,
        surfaceForm: payload.surfaceForm,
        senses: payload.senses.map((sense) => ({
          partOfSpeech: sense.partOfSpeech,
          text: sense.text,
          example: sense.example,
        })),
        sourceName: payload.sourceName,
        isSaved: payload.isSaved,
        found: payload.found,
      });
    });

    this.#connection.on("ReturnTranslation", (payload: TranslationWire) => {
      callbacks.onTranslation(payload.surfaceForm, {
        text: payload.text,
        note: payload.targetLanguage,
        error: payload.error,
      });
    });

    this.#connection.on("ReturnVocabularyChanged", (payload: VocabularyChangedWire) => {
      callbacks.onVocabularyChanged(payload.term, payload.isSaved);
    });
  }

  /** Connects, and keeps trying until it does. Resolves once the hub has answered. */
  async start(): Promise<void> {
    await this.#connectWithRetry();
  }

  /** The reading session. Anything that goes wrong here is fatal to the page, so it throws. */
  async getReadingSession(): Promise<ReadingSessionView> {
    return await this.#call<ReadingSessionView>("GetReadingSession");
  }

  /**
   * Searches the source site's catalogue. A failed search is an empty list rather than a
   * thrown error — the menu shows "nothing found", which is what the reader needs either way.
   */
  async searchNovels(query: string): Promise<readonly NovelSearchResult[]> {
    try {
      return await this.#call<NovelSearchResult[]>("SearchNovels", query);
    } catch (error: unknown) {
      console.error(`Novel search failed for '${query}':`, error);
      return [];
    }
  }

  /** Where this reader left off in one novel, for opening it from the library menu. */
  async getNovelProgress(novelName: string): Promise<ReadingSessionView> {
    return await this.#call<ReadingSessionView>("GetNovelProgress", novelName);
  }

  /**
   * A whole chapter. A chapter that would not load comes back with `found: false` and no
   * paragraphs rather than throwing — the source site answers 200 with an empty page often
   * enough that the page has to cope with it. A call that never got through is marked
   * `failed` as well, so the page can retry it instead of reading it as the end of the
   * novel (D28).
   */
  async loadChapter(novelName: string, chapterNumber: number): Promise<ChapterView> {
    try {
      const chapter = await this.#call<ChapterWire>("LoadChapter", novelName, chapterNumber);
      return { ...chapter, failed: false };
    } catch (error: unknown) {
      console.error(`Could not load chapter ${chapterNumber} of '${novelName}':`, error);
      return { novelName, chapterNumber, paragraphs: [], found: false, failed: true };
    }
  }

  /** Fire-and-forget bookmark; a lost one costs the reader nothing but a scroll. */
  reportProgress(novelName: string, chapterNumber: number, paragraphNumber: number): void {
    void this.#send("ReportProgress", novelName, chapterNumber, paragraphNumber);
  }

  requestDefinition(surfaceForm: string): void {
    void this.#send("GetDefinition", surfaceForm);
  }

  saveWord(novelName: string, surfaceForm: string): void {
    void this.#send("SaveWord", novelName, surfaceForm);
  }

  deleteWord(surfaceForm: string): void {
    void this.#send("DeleteWord", surfaceForm);
  }

  /**
   * Asks for a translation. `settings` is passed only on the very first one a reader ever
   * requests, which is sent alongside the save that stores them — every later call leaves it
   * out and the server uses what it stored (D31).
   */
  requestTranslation(surfaceForm: string, settings?: { email: string; language: string }): void {
    void this.#send("Translate", surfaceForm, settings?.email ?? null, settings?.language ?? null);
  }

  /** Stores the reader's translation settings. Answers with the field to fix, or nothing. */
  async saveTranslationSettings(email: string, language: string): Promise<TranslationSettingsResult> {
    try {
      return await this.#call<TranslationSettingsWire>("SaveTranslationSettings", email, language);
    } catch (error: unknown) {
      console.error("Could not save translation settings:", error);
      return { email: null, language: null, error: "unavailable" };
    }
  }

  /**
   * Every call goes through here, so none of them is thrown away just because it was made
   * during a gap: a call waits for the connection to come back before it is sent (D28).
   */
  async #call<T>(method: string, ...args: unknown[]): Promise<T> {
    await this.#whenConnected(connectionWaitInMilliseconds);
    return await this.#connection.invoke<T>(method, ...args);
  }

  /** A failed hub call is logged, never thrown at the reader mid-chapter. */
  async #send(method: string, ...args: unknown[]): Promise<void> {
    try {
      await this.#call(method, ...args);
    } catch (error: unknown) {
      console.error(`Hub call '${method}' failed:`, error);
    }
  }

  /**
   * Connects, waiting a second between attempts, until the hub answers. An expired session
   * is the one thing worth stopping for: no amount of retrying will fix a missing cookie, so
   * the reader is sent to the login screen.
   */
  async #connectWithRetry(closeError?: Error): Promise<void> {
    if (this.#connecting) return;
    this.#connecting = true;

    try {
      if (this.#returnToLoginIfSessionExpired(closeError)) return;

      for (;;) {
        if (this.#connection.state === signalR.HubConnectionState.Connected) {
          this.#setState("connected");
          return;
        }

        try {
          await this.#connection.start();
          this.#setState("connected");
          return;
        } catch (error: unknown) {
          if (this.#returnToLoginIfSessionExpired(error)) return;

          console.error("Could not reach the hub; trying again:", error);
          this.#setState("reconnecting");
          await delay(reconnectDelayInMilliseconds);
        }
      }
    } finally {
      this.#connecting = false;
    }
  }

  #returnToLoginIfSessionExpired(error: unknown): boolean {
    if (!isSessionExpired(error)) return false;

    window.location.href = "/Login";
    return true;
  }

  /**
   * Resolves as soon as the connection is up, or after `timeoutInMilliseconds` if it is not.
   * The call that was waiting then fails in the ordinary way and the page retries it — a
   * wait that never ended would leave the chapter loader wedged instead.
   */
  async #whenConnected(timeoutInMilliseconds: number): Promise<void> {
    if (this.#connection.state === signalR.HubConnectionState.Connected) return;

    await new Promise<void>((resolve) => {
      const waiter = (): void => {
        clearTimeout(timer);
        resolve();
      };

      const timer = window.setTimeout(() => {
        this.#waiters.delete(waiter);
        resolve();
      }, timeoutInMilliseconds);

      this.#waiters.add(waiter);
    });
  }

  #setState(state: ConnectionState): void {
    const changed = this.#state !== state;
    this.#state = state;

    if (state === "connected") {
      for (const waiter of [...this.#waiters]) waiter();
      this.#waiters.clear();
    }

    if (changed) this.#callbacks.onConnectionStateChanged(state);
  }
}
