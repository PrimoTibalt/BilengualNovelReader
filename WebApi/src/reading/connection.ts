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
  readonly text: string;
  readonly targetLanguage: string | null;
  readonly isStub: boolean;
}

interface VocabularyChangedWire {
  readonly term: string;
  readonly isSaved: boolean;
}

export interface ParagraphView {
  readonly number: number;
  readonly markup: string;
}

export interface ChapterView {
  readonly novelName: string;
  readonly chapterNumber: number;
  readonly paragraphs: readonly ParagraphView[];
  readonly found: boolean;
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
}

export interface ReaderConnectionCallbacks {
  onDefinition(view: DefinitionView): void;
  onTranslation(term: string, translation: TranslationView): void;
  onVocabularyChanged(term: string, isSaved: boolean): void;
}

export class ReaderConnection {
  readonly #connection: SignalR.HubConnection;

  constructor(callbacks: ReaderConnectionCallbacks) {
    this.#connection = new signalR.HubConnectionBuilder().withUrl("/signalr").build();

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
      // The stub answer is labelled as such, so a placeholder can never read as a real
      // translation (D10).
      const note = payload.isStub
        ? "placeholder — no translation provider configured"
        : payload.targetLanguage;

      callbacks.onTranslation(payload.term, { text: payload.text, note });
    });

    this.#connection.on("ReturnVocabularyChanged", (payload: VocabularyChangedWire) => {
      callbacks.onVocabularyChanged(payload.term, payload.isSaved);
    });
  }

  async start(): Promise<void> {
    await this.#connection.start();
  }

  /** The reading session. Anything that goes wrong here is fatal to the page, so it throws. */
  async getReadingSession(): Promise<ReadingSessionView> {
    return await this.#connection.invoke<ReadingSessionView>("GetReadingSession");
  }

  /**
   * Searches the source site's catalogue. A failed search is an empty list rather than a
   * thrown error — the menu shows "nothing found", which is what the reader needs either way.
   */
  async searchNovels(query: string): Promise<readonly NovelSearchResult[]> {
    try {
      return await this.#connection.invoke<NovelSearchResult[]>("SearchNovels", query);
    } catch (error: unknown) {
      console.error(`Novel search failed for '${query}':`, error);
      return [];
    }
  }

  /** Where this reader left off in one novel, for opening it from the library menu. */
  async getNovelProgress(novelName: string): Promise<ReadingSessionView> {
    return await this.#connection.invoke<ReadingSessionView>("GetNovelProgress", novelName);
  }

  /**
   * A whole chapter. A chapter that would not load comes back with `found: false` and no
   * paragraphs rather than throwing — the source site answers 200 with an empty page often
   * enough that the page has to cope with it.
   */
  async loadChapter(novelName: string, chapterNumber: number): Promise<ChapterView> {
    try {
      return await this.#connection.invoke<ChapterView>("LoadChapter", novelName, chapterNumber);
    } catch (error: unknown) {
      console.error(`Could not load chapter ${chapterNumber} of '${novelName}':`, error);
      return { novelName, chapterNumber, paragraphs: [], found: false };
    }
  }

  /** Fire-and-forget bookmark; a lost one costs the reader nothing but a scroll. */
  reportProgress(novelName: string, chapterNumber: number, paragraphNumber: number): void {
    void this.#invoke("ReportProgress", novelName, chapterNumber, paragraphNumber);
  }

  requestDefinition(surfaceForm: string): void {
    void this.#invoke("GetDefinition", surfaceForm);
  }

  saveWord(novelName: string, surfaceForm: string): void {
    void this.#invoke("SaveWord", novelName, surfaceForm);
  }

  deleteWord(surfaceForm: string): void {
    void this.#invoke("DeleteWord", surfaceForm);
  }

  requestTranslation(surfaceForm: string): void {
    void this.#invoke("Translate", surfaceForm);
  }

  /** A failed hub call is logged, never thrown at the reader mid-chapter. */
  async #invoke(method: string, ...args: unknown[]): Promise<void> {
    try {
      await this.#connection.invoke(method, ...args);
    } catch (error: unknown) {
      console.error(`Hub call '${method}' failed:`, error);
    }
  }
}
