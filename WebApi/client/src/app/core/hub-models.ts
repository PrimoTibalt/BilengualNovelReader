/**
 * The hub's wire shapes, camelCased by SignalR's JSON protocol, and the views the page builds
 * from them. No call carries a user name: the server takes the reader from the cookie (D20).
 */

export interface DefinitionSenseView {
  readonly partOfSpeech: string | null;
  readonly text: string;
  readonly example: string | null;
}

export interface DefinitionView {
  readonly term: string;
  readonly surfaceForm: string;
  readonly senses: readonly DefinitionSenseView[];
  readonly sourceName: string | null;
  readonly isSaved: boolean;
  readonly found: boolean;
}

export interface DefinitionWire extends DefinitionView {
  readonly sourceUrl: string | null;
}

export interface TranslationWire {
  readonly term: string;
  /** What was selected, echoed back — this is what an answer is matched against (D32). */
  readonly surfaceForm: string;
  readonly text: string | null;
  readonly targetLanguage: string | null;
  /** A code, not a sentence: `not-configured` opens the settings form (D31). */
  readonly error: string | null;
  /** Who translated it: `MyMemory`, or the reader's AI model (D35). */
  readonly provider: string | null;
}

/**
 * A translation as the page shows it. Exactly one of `text` and `error` is set; `note` carries
 * the language it came back in.
 */
export interface TranslationView {
  readonly text: string | null;
  readonly note: string | null;
  readonly error: string | null;
}

export interface PasswordChangeResult {
  /** Null when the password was changed; otherwise why not. */
  readonly error: string | null;
  /** With `locked`: how long until the next try (D41). */
  readonly retryAfterMinutes: number | null;
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

/** One AI service the reader may bring a key for (D35). */
export interface AssistantServiceView {
  readonly id: string;
  readonly name: string;
}

/**
 * The reader's AI as the server describes it — never the key, only its last four characters.
 * `serviceId` null means none is set up. `error` names the field a save was refused for.
 */
export interface AssistantSettingsView {
  readonly services: readonly AssistantServiceView[];
  readonly serviceId: string | null;
  readonly model: string | null;
  readonly keyHint: string | null;
  readonly error: string | null;
}

export interface VocabularyChange {
  /** The word as saved. */
  readonly term: string;
  readonly isSaved: boolean;
  /** Every form it is matched in — `run`, `runs`, `ran`, `running` … (D39). */
  readonly forms: readonly string[];
}

export interface ParagraphView {
  readonly number: number;
  readonly markup: string;
}

/** A chapter as the hub answers it. */
export interface ChapterWire {
  readonly novelName: string;
  readonly chapterNumber: number;
  readonly paragraphs: readonly ParagraphView[];
  readonly found: boolean;
}

export interface ChapterView extends ChapterWire {
  /**
   * True when the *call* did not get through, as opposed to the server saying there is no
   * such chapter. An empty answer is the end of the novel, a failed call is a connection to
   * wait out and retry (D28).
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
  readonly assistant: AssistantSettingsView;
}

/**
 * What the page shows about the link to the server: connected, or trying to get back. There
 * is no third state — the client never stops trying (D28).
 */
export type ConnectionState = "connected" | "reconnecting";

// ---- The vocabulary review ----

/** Failure codes the review answers with; the page owns the wording. */
export type ReviewError = "ai-required" | "exercise-unknown" | "ai-unavailable" | "answer-invalid" | "unreachable";

export interface ReviewSummaryView {
  readonly due: number;
  readonly new: number;
  readonly total: number;
  readonly nextDueUtc: string | null;
  readonly error: ReviewError | null;
}

export type ExerciseKind = "match" | "recall" | "sentence" | "translate";

export interface MatchItemView {
  readonly id: string;
  readonly text: string;
}

/** The next exercise; `kind` null with no error means nothing is due until `nextDueUtc`. */
export interface ReviewExerciseView {
  readonly id: string | null;
  readonly kind: ExerciseKind | null;
  readonly words: readonly MatchItemView[];
  readonly definitions: readonly MatchItemView[];
  /** The word asked about, or for a translation the sentence to translate. */
  readonly prompt: string | null;
  readonly languageName: string | null;
  readonly remaining: number;
  readonly nextDueUtc: string | null;
  readonly error: ReviewError | null;
}

export type ReviewRatingName = "again" | "hard" | "good" | "easy";

export interface GradedWordView {
  readonly word: string;
  readonly rating: ReviewRatingName;
  readonly nextDueUtc: string;
}

export interface MatchCheckView {
  readonly correct: boolean;
  readonly completed: boolean;
  readonly graded: readonly GradedWordView[];
  readonly error: ReviewError | null;
}

export type AnswerVerdict = "correct" | "partial" | "wrong";

export interface AnswerOutcomeView {
  readonly verdict: AnswerVerdict | null;
  readonly feedback: string | null;
  readonly reference: string | null;
  readonly graded: GradedWordView | null;
  readonly error: ReviewError | null;
}

export interface GiveUpView {
  readonly reference: string | null;
  /** A match's word ids mapped to their definition ids. */
  readonly solution: Readonly<Record<string, string>>;
  readonly graded: readonly GradedWordView[];
  readonly error: ReviewError | null;
}
