import { Injectable, signal } from "@angular/core";
import * as signalR from "@microsoft/signalr";
import { Subject } from "rxjs";
import { appUrl } from "./app-base";
import type {
  AnswerOutcomeView,
  AssistantSettingsView,
  BookContentsView,
  GiveUpView,
  MatchCheckView,
  ReviewExerciseView,
  ReviewSummaryView,
  ChapterView,
  ChapterWire,
  ConnectionState,
  DefinitionView,
  DefinitionWire,
  NovelSearchResult,
  PasswordChangeResult,
  ReadingSessionView,
  TranslationSettingsResult,
  TranslationView,
  TranslationWire,
  VocabularyChange,
} from "./hub-models";

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

/**
 * The page's one link to the server. Calls that return an answer are awaited; definitions,
 * translations and vocabulary changes are pushed, because they are notifications (D19).
 */
@Injectable({ providedIn: "root" })
export class ReaderConnection {
  readonly #connection: signalR.HubConnection;
  /** Calls parked until the connection is back; resolved by {@link #setState}. */
  readonly #waiters = new Set<() => void>();
  /** Guards against two connect loops running at once. */
  #connecting = false;

  readonly #state = signal<ConnectionState | undefined>(undefined);
  /** Unset until the first attempt settles, so that first answer always reaches the page. */
  readonly state = this.#state.asReadonly();

  readonly definitions = new Subject<DefinitionView>();
  /** Keyed by the surface form that was asked for, not the term it normalised to (D32). */
  readonly translations = new Subject<{ surfaceForm: string; translation: TranslationView }>();
  readonly vocabularyChanges = new Subject<VocabularyChange>();

  constructor() {
    this.#connection = new signalR.HubConnectionBuilder()
      .withUrl(appUrl("signalr"))
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
      this.definitions.next({
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
      this.translations.next({
        surfaceForm: payload.surfaceForm,
        translation: {
          text: payload.text,
          // The language, and who it came from once that is worth saying (D35).
          note: payload.provider === null || payload.targetLanguage === null
            ? payload.targetLanguage
            : `${payload.targetLanguage} · ${payload.provider}`,
          error: payload.error,
        },
      });
    });

    this.#connection.on("ReturnVocabularyChanged", (payload: VocabularyChange) => {
      this.vocabularyChanges.next({ ...payload, forms: payload.forms ?? [payload.term] });
    });
  }

  /** Connects, and keeps trying until it does. Resolves once the hub has answered. */
  async start(): Promise<void> {
    await this.#connectWithRetry();
  }

  /** The reading session. Anything that goes wrong here is fatal to the page, so it throws. */
  async getReadingSession(): Promise<ReadingSessionView> {
    return await this.call<ReadingSessionView>("GetReadingSession");
  }

  /**
   * Searches the source site's catalogue. A failed search is an empty list rather than a
   * thrown error — the menu shows "nothing found", which is what the reader needs either way.
   */
  async searchNovels(query: string): Promise<readonly NovelSearchResult[]> {
    try {
      return await this.call<NovelSearchResult[]>("SearchNovels", query);
    } catch (error: unknown) {
      console.error(`Novel search failed for '${query}':`, error);
      return [];
    }
  }

  /** Where this reader left off in one novel, for opening it from the library menu. */
  async getNovelProgress(novelName: string): Promise<ReadingSessionView> {
    return await this.call<ReadingSessionView>("GetNovelProgress", novelName);
  }

  /**
   * An uploaded book's table of contents, or null for a site novel — and for a call that did not
   * get through, after which the chapter menu simply falls back to numbers (D50).
   */
  async getBookContents(novelName: string): Promise<BookContentsView | null> {
    try {
      return await this.call<BookContentsView | null>("GetBookContents", novelName);
    } catch (error: unknown) {
      console.error(`Could not load the contents of '${novelName}':`, error);
      return null;
    }
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
      const chapter = await this.call<ChapterWire>("LoadChapter", novelName, chapterNumber);
      return { ...chapter, failed: false };
    } catch (error: unknown) {
      console.error(`Could not load chapter ${chapterNumber} of '${novelName}':`, error);
      return { novelName, chapterNumber, paragraphs: [], found: false, failed: true };
    }
  }

  /**
   * Sends a bookmark; true once the server has it. The page keeps every bookmark on the device
   * as well and only marks it sent on true, so one made offline is sent again later (D51).
   */
  async reportProgress(novelName: string, chapterNumber: number, paragraphNumber: number): Promise<boolean> {
    try {
      await this.call("ReportProgress", novelName, chapterNumber, paragraphNumber);
      return true;
    } catch (error: unknown) {
      console.warn("Bookmark not sent:", error);
      return false;
    }
  }

  /**
   * A chapter to keep on the device (D51): answered like {@link loadChapter}, without the
   * server warming the next one — the download asks for each chapter itself.
   */
  async downloadChapter(novelName: string, chapterNumber: number): Promise<ChapterView> {
    try {
      const chapter = await this.call<ChapterWire>("DownloadChapter", novelName, chapterNumber);
      return { ...chapter, failed: false };
    } catch (error: unknown) {
      console.error(`Could not download chapter ${chapterNumber} of '${novelName}':`, error);
      return { novelName, chapterNumber, paragraphs: [], found: false, failed: true };
    }
  }

  /** Whether calls go straight through now, rather than waiting for the connection. */
  isConnected(): boolean {
    return this.#connection.state === signalR.HubConnectionState.Connected;
  }

  /**
   * Starts connecting, and says whether it is up within `milliseconds`. The attempt carries on
   * either way; the page reads offline meanwhile and catches up when it lands (D51).
   */
  async startWithin(milliseconds: number): Promise<boolean> {
    const started = this.#connectWithRetry().then(() => this.isConnected());
    return await Promise.race([started, delay(milliseconds).then(() => this.isConnected())]);
  }

  /**
   * `paragraph` is the text the selection was made in. The reader's AI reads the word against
   * it (D35); without one, the server picks the dictionary sense it uses (D47).
   */
  requestDefinition(surfaceForm: string, paragraph: string | null): void {
    void this.#send("GetDefinition", surfaceForm, paragraph);
  }

  /** Settles once the server has answered, or the call has failed (D48). */
  saveWord(novelName: string, surfaceForm: string): Promise<void> {
    return this.#send("SaveWord", novelName, surfaceForm);
  }

  deleteWord(surfaceForm: string): Promise<void> {
    return this.#send("DeleteWord", surfaceForm);
  }

  /**
   * Asks for a translation. `settings` is passed only on the very first one a reader ever
   * requests, which is sent alongside the save that stores them — every later call leaves it
   * out and the server uses what it stored (D31). `paragraph` is sent only while the reader has an AI;
   * MyMemory translates the selection alone.
   */
  requestTranslation(
    surfaceForm: string,
    paragraph: string | null,
    settings?: { email: string; language: string },
  ): void {
    void this.#send("Translate", surfaceForm, settings?.email ?? null, settings?.language ?? null, paragraph);
  }

  /** Stores the reader's AI. An empty key keeps the one already stored. */
  async saveAssistantSettings(serviceId: string, model: string, apiKey: string): Promise<AssistantSettingsView | undefined> {
    try {
      return await this.call<AssistantSettingsView>("SaveAssistantSettings", serviceId, model, apiKey);
    } catch (error: unknown) {
      console.error("Could not save AI settings:", error);
      return undefined;
    }
  }

  /** Forgets the reader's AI, key and all. */
  async removeAssistantSettings(): Promise<AssistantSettingsView | undefined> {
    try {
      return await this.call<AssistantSettingsView>("RemoveAssistantSettings");
    } catch (error: unknown) {
      console.error("Could not remove AI settings:", error);
      return undefined;
    }
  }

  /**
   * Sets the reader's password, given their current one. Undefined when the server could not
   * be reached.
   */
  async changePassword(currentPassword: string, newPassword: string): Promise<PasswordChangeResult | undefined> {
    try {
      return await this.call<PasswordChangeResult>("ChangePassword", currentPassword, newPassword);
    } catch (error: unknown) {
      console.error("Could not change the password:", error);
      return undefined;
    }
  }

  /** Stores the reader's translation settings. Answers with the field to fix, or nothing. */
  async saveTranslationSettings(email: string, language: string): Promise<TranslationSettingsResult> {
    try {
      return await this.call<TranslationSettingsResult>("SaveTranslationSettings", email, language);
    } catch (error: unknown) {
      console.error("Could not save translation settings:", error);
      return { email: null, language: null, error: "unavailable" };
    }
  }

  // ---- The vocabulary review. A call that never got through answers `unreachable`. ----

  async getReviewSummary(): Promise<ReviewSummaryView> {
    return await this.#review("GetReviewSummary", { due: 0, new: 0, total: 0, nextDueUtc: null });
  }

  async nextReviewExercise(): Promise<ReviewExerciseView> {
    return await this.#review("NextReviewExercise", {
      id: null, kind: null, words: [], definitions: [], prompt: null, languageName: null, remaining: 0, nextDueUtc: null,
    });
  }

  async checkReviewMatch(exerciseId: string, wordId: string, definitionId: string): Promise<MatchCheckView> {
    return await this.#review("CheckReviewMatch", { correct: false, completed: false, graded: [] }, exerciseId, wordId, definitionId);
  }

  async submitReviewAnswer(exerciseId: string, answer: string): Promise<AnswerOutcomeView> {
    return await this.#review("SubmitReviewAnswer", { verdict: null, feedback: null, reference: null, graded: null }, exerciseId, answer);
  }

  async giveUpReviewExercise(exerciseId: string): Promise<GiveUpView> {
    return await this.#review("GiveUpReviewExercise", { reference: null, solution: {}, graded: [] }, exerciseId);
  }

  async #review<T extends { readonly error: string | null }>(method: string, empty: Omit<T, "error">, ...args: unknown[]): Promise<T> {
    try {
      return await this.call<T>(method, ...args);
    } catch (error: unknown) {
      console.error(`Review call '${method}' failed:`, error);
      return { ...empty, error: "unreachable" } as T;
    }
  }

  /**
   * Every call goes through here, so none of them is thrown away just because it was made
   * during a gap: a call waits for the connection to come back before it is sent (D28).
   */
  async call<T>(method: string, ...args: unknown[]): Promise<T> {
    await this.#whenConnected(connectionWaitInMilliseconds);
    return await this.#connection.invoke<T>(method, ...args);
  }

  /** A failed hub call is logged, never thrown at the reader mid-chapter. */
  async #send(method: string, ...args: unknown[]): Promise<void> {
    try {
      await this.call(method, ...args);
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

    window.location.href = appUrl("Login");
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
    if (state === "connected") {
      for (const waiter of [...this.#waiters]) waiter();
      this.#waiters.clear();
    }

    this.#state.set(state);
  }
}
