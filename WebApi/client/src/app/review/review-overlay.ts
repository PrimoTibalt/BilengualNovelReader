import {
  ChangeDetectionStrategy,
  Component,
  type ElementRef,
  Injector,
  afterNextRender,
  afterRenderEffect,
  computed,
  inject,
  output,
  signal,
  viewChild,
} from "@angular/core";
import type {
  AnswerVerdict,
  ExerciseKind,
  GradedWordView,
  MatchItemView,
  ReviewError,
  ReviewExerciseView,
} from "../core/hub-models";
import { isTouchPrimary } from "../core/pointer";
import { ReaderConnection } from "../core/reader-connection";
import { relativeTime } from "./relative-time";
import { listen, speechInputAvailable } from "./speech-input";

/**
 * The vocabulary review, drawn over the page like the other TUI panels.
 *
 * It decides nothing. The server picks the words, the form of each exercise and whether an
 * answer was right; the panel shows what it is given and sends back what the reader did. A
 * match is checked a pair at a time, so the key never reaches the page.
 *
 * Keys come through the KeyboardRouter like every other panel's (D6): the page binds them to
 * {@link move}, {@link across}, {@link activate} and the rest. The answer field owns its own
 * keys while focused — Enter checks, Shift+Enter starts a new line, Escape leaves the field.
 */

type Phase = "loading" | "exercise" | "checking" | "result" | "done" | "failed";
type Side = "words" | "definitions";

interface MatchEntry extends MatchItemView {
  readonly matched: boolean;
}

/** What the result screen shows, from a judged answer, a finished match or a give-up. */
interface ResultView {
  readonly verdict: AnswerVerdict | "matched" | "shown";
  readonly feedback: string | null;
  readonly reference: string | null;
  /** A given-up match: each word with its definition. */
  readonly solution: readonly { readonly word: string; readonly definition: string }[];
  readonly graded: readonly GradedWordView[];
}

/** How long a wrong pair stays marked before it can be tried again. */
const wrongFlashInMilliseconds = 700;

const instructions: Record<ExerciseKind, string> = {
  match: "match each word to its meaning",
  recall: "what does it mean? say it in your own words",
  sentence: "use it in a sentence of your own",
  translate: "translate into English",
};

const verdictLabels: Record<ResultView["verdict"], string> = {
  correct: "✓ correct",
  partial: "≈ nearly",
  wrong: "✗ not quite",
  matched: "✓ all matched",
  shown: "the answer",
};

/** Failure codes are the server's; the wording is the page's. */
function describeError(error: ReviewError): string {
  switch (error) {
    case "ai-required":
      return "The review needs your AI — set one up in the settings drawer.";
    case "ai-unavailable":
      return "Your AI did not answer — try again in a moment.";
    case "answer-invalid":
      return "Write an answer first, or press “don't know”.";
    case "exercise-unknown":
      return "That exercise has expired — moving on.";
    default:
      return "Could not reach the server — try again.";
  }
}

@Component({
  selector: "app-review-overlay",
  templateUrl: "./review-overlay.html",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewOverlay {
  readonly #connection = inject(ReaderConnection);
  readonly #injector = inject(Injector);

  /** Told when the review opens and closes, so the page can take and give back the keys. */
  readonly opened = output<void>();
  readonly closed = output<void>();

  readonly #open = signal(false);
  readonly isOpen = this.#open.asReadonly();

  protected readonly phase = signal<Phase>("loading");
  protected readonly exercise = signal<ReviewExerciseView | undefined>(undefined);
  protected readonly message = signal("");
  protected readonly result = signal<ResultView | undefined>(undefined);
  protected readonly nextDue = signal<string | null>(null);

  // ---- A match ----
  protected readonly words = signal<readonly MatchEntry[]>([]);
  protected readonly definitions = signal<readonly MatchEntry[]>([]);
  protected readonly side = signal<Side>("words");
  protected readonly cursor = signal(0);
  /** The item picked first, waiting for its partner on the other side. */
  protected readonly picked = signal<{ side: Side; id: string } | undefined>(undefined);
  /** The pair just joined wrongly, marked for a moment. */
  protected readonly wrong = signal<readonly string[]>([]);

  // ---- A free answer ----
  protected readonly answer = signal("");
  protected readonly editing = signal(false);
  protected readonly listening = signal(false);
  #listener: { stop(): void } | undefined;

  protected readonly speechAvailable = speechInputAvailable;
  protected readonly isTouch = isTouchPrimary;
  protected readonly verdictLabels = verdictLabels;

  protected readonly answerField = viewChild<ElementRef<HTMLTextAreaElement>>("answerField");
  protected readonly body = viewChild<ElementRef<HTMLElement>>("body");

  protected readonly kind = computed(() => this.exercise()?.kind ?? null);
  protected readonly instruction = computed(() => {
    const exercise = this.exercise();
    if (!exercise?.kind) return "";
    return exercise.kind === "translate" && exercise.languageName
      ? `translate from ${exercise.languageName} into English`
      : instructions[exercise.kind];
  });

  protected readonly counter = computed(() => {
    const remaining = this.exercise()?.remaining ?? 0;
    return this.phase() === "done" || remaining === 0 ? "" : `${remaining} due`;
  });

  protected readonly hints = computed(() => {
    switch (this.phase()) {
      case "exercise":
        if (this.kind() === "match") return "j/k move  ·  h/l side  ·  enter pick  ·  x don't know  ·  esc close";
        if (this.editing()) return "enter check  ·  shift+enter new line  ·  esc done";
        return ["enter answer", this.speechAvailable ? "v speak" : null, "x don't know", "esc close"]
          .filter((part) => part !== null).join("  ·  ");
      case "result":
        return "enter next  ·  esc close";
      case "failed":
        return "enter retry  ·  esc close";
      default:
        return "esc close";
    }
  });

  protected readonly matchedAll = computed(() => this.words().length > 0 && this.words().every((word) => word.matched));

  constructor() {
    // The cursor stays in view as it moves down a long list of definitions.
    afterRenderEffect(() => {
      const side = this.side();
      const cursor = this.cursor();
      this.body()?.nativeElement
        .querySelector(`.review-match__column--${side} .review-match__item:nth-child(${cursor + 1})`)
        ?.scrollIntoView({ block: "nearest" });
    });
  }

  open(): void {
    if (this.#open()) return;
    this.#open.set(true);
    this.opened.emit();
    void this.#next();
  }

  close(): void {
    if (!this.#open()) return;
    this.#stopListening();
    this.#open.set(false);
    this.exercise.set(undefined);
    this.result.set(undefined);
    this.closed.emit();
  }

  // ---- Keys, bound by the page ----

  /** j/k: down and up the current side of a match. */
  move(step: 1 | -1): void {
    if (this.phase() !== "exercise" || this.kind() !== "match") return;

    const count = (this.side() === "words" ? this.words() : this.definitions()).length;
    if (count === 0) return;
    this.cursor.update((cursor) => (cursor + step + count) % count);
  }

  /** h/l: across to the other side of a match, landing on its first item still to join. */
  across(): void {
    if (this.phase() !== "exercise" || this.kind() !== "match") return;

    const side: Side = this.side() === "words" ? "definitions" : "words";
    this.#moveTo(side);
  }

  /** Enter: pick, answer, or go on — whatever the screen is waiting for. */
  activate(): void {
    switch (this.phase()) {
      case "exercise":
        if (this.kind() === "match") {
          const items = this.side() === "words" ? this.words() : this.definitions();
          const item = items[this.cursor()];
          if (item) void this.pick(this.side(), item);
        } else {
          this.focusAnswer();
        }
        return;
      case "result":
      case "failed":
        void this.#next();
        return;
      case "done":
        this.close();
        return;
      default:
        return;
    }
  }

  /** `v`: start or stop dictating. */
  toggleVoice(): void {
    if (this.phase() !== "exercise" || this.kind() === "match" || !this.speechAvailable) return;

    if (this.#listener) {
      this.#stopListening();
      return;
    }

    const before = this.answer().trim();
    this.listening.set(true);
    this.#listener = listen({
      onText: (settled, interim) => this.answer.set([before, settled, interim].filter((part) => part.length > 0).join(" ")),
      onEnd: (error) => {
        this.#listener = undefined;
        this.listening.set(false);
        if (error !== null && error !== "no-speech" && error !== "aborted") {
          this.message.set(error === "not-allowed" ? "The microphone is blocked for this site." : `Dictation stopped: ${error}.`);
        }
      },
    });
  }

  /** `x`: "don't know" — the answer is shown, and the word starts over. */
  async giveUp(): Promise<void> {
    const exercise = this.exercise();
    if (this.phase() !== "exercise" || !exercise?.id) return;

    this.#stopListening();
    this.phase.set("checking");
    const answer = await this.#connection.giveUpReviewExercise(exercise.id);
    if (answer.error !== null) {
      this.#fail(answer.error);
      return;
    }

    const definitions = new Map(exercise.definitions.map((definition) => [definition.id, definition.text]));
    this.result.set({
      verdict: "shown",
      feedback: null,
      reference: answer.reference,
      solution: exercise.words.map((word) => ({ word: word.text, definition: definitions.get(answer.solution[word.id] ?? "") ?? "" })),
      graded: answer.graded,
    });
    this.phase.set("result");
  }

  // ---- A match ----

  /**
   * A tap or Enter on an item. The first pick waits for one from the other side; picking a
   * second item on the same side changes the pick. A pair is checked by the server, which
   * marks a wrong one against the word.
   */
  protected async pick(side: Side, item: MatchEntry): Promise<void> {
    const exercise = this.exercise();
    if (this.phase() !== "exercise" || !exercise?.id || item.matched) return;

    this.side.set(side);
    this.cursor.set((side === "words" ? this.words() : this.definitions()).indexOf(item));

    const picked = this.picked();
    if (!picked || picked.side === side) {
      this.picked.set({ side, id: item.id });
      // Straight on to the other side, where the partner is.
      this.#moveTo(side === "words" ? "definitions" : "words");
      return;
    }

    const wordId = side === "words" ? item.id : picked.id;
    const definitionId = side === "definitions" ? item.id : picked.id;
    this.picked.set(undefined);

    const check = await this.#connection.checkReviewMatch(exercise.id, wordId, definitionId);
    if (check.error !== null) {
      this.#fail(check.error);
      return;
    }

    if (!check.correct) {
      this.wrong.set([wordId, definitionId]);
      setTimeout(() => this.wrong.set([]), wrongFlashInMilliseconds);
      this.#moveTo("words", wordId);
      return;
    }

    this.words.update((words) => words.map((word) => (word.id === wordId ? { ...word, matched: true } : word)));
    this.definitions.update((definitions) =>
      definitions.map((definition) => (definition.id === definitionId ? { ...definition, matched: true } : definition)));

    if (check.completed) {
      this.result.set({ verdict: "matched", feedback: null, reference: null, solution: [], graded: check.graded });
      this.phase.set("result");
      return;
    }

    this.#moveTo("words");
  }

  protected isWrong(id: string): boolean {
    return this.wrong().includes(id);
  }

  /** The cursor onto a side: onto `id` when given, else its first item still to join. */
  #moveTo(side: Side, id?: string): void {
    const items = side === "words" ? this.words() : this.definitions();
    const index = id !== undefined
      ? items.findIndex((item) => item.id === id)
      : items.findIndex((item) => !item.matched);
    this.side.set(side);
    this.cursor.set(Math.max(0, index));
  }

  // ---- A free answer ----

  focusAnswer(): void {
    this.answerField()?.nativeElement.focus();
  }

  protected onAnswerKey(event: KeyboardEvent, field: HTMLTextAreaElement): void {
    if (event.key === "Escape") {
      event.preventDefault();
      // Out of the field only; a second Escape closes the review.
      event.stopPropagation();
      field.blur();
      return;
    }

    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      void this.submit();
    }
  }

  protected async submit(): Promise<void> {
    const exercise = this.exercise();
    const answer = this.answer().trim();
    if (this.phase() !== "exercise" || !exercise?.id) return;
    if (answer.length === 0) {
      this.message.set(describeError("answer-invalid"));
      return;
    }

    this.#stopListening();
    this.answerField()?.nativeElement.blur();
    this.message.set("");
    this.phase.set("checking");

    const outcome = await this.#connection.submitReviewAnswer(exercise.id, answer);
    if (outcome.error === "ai-unavailable" || outcome.error === "unreachable") {
      // Nothing was graded; the answer stays for another try.
      this.phase.set("exercise");
      this.message.set(describeError(outcome.error));
      return;
    }
    if (outcome.error !== null || outcome.verdict === null) {
      this.#fail(outcome.error ?? "exercise-unknown");
      return;
    }

    this.result.set({
      verdict: outcome.verdict,
      feedback: outcome.feedback,
      reference: outcome.reference,
      solution: [],
      graded: outcome.graded ? [outcome.graded] : [],
    });
    this.phase.set("result");
  }

  #stopListening(): void {
    this.#listener?.stop();
    this.#listener = undefined;
    this.listening.set(false);
  }

  // ---- Moving on ----

  protected when(graded: GradedWordView): string {
    return relativeTime(graded.nextDueUtc);
  }

  protected nextDueText(): string {
    const next = this.nextDue();
    return next === null ? "Nothing is due." : `Nothing is due. The next word comes back ${relativeTime(next)}.`;
  }

  async #next(): Promise<void> {
    this.phase.set("loading");
    this.message.set("");
    this.result.set(undefined);
    this.answer.set("");
    this.picked.set(undefined);
    this.wrong.set([]);

    const exercise = await this.#connection.nextReviewExercise();
    if (!this.#open()) return;

    if (exercise.error !== null) {
      this.#fail(exercise.error);
      return;
    }

    if (exercise.kind === null) {
      this.exercise.set(undefined);
      this.nextDue.set(exercise.nextDueUtc);
      this.phase.set("done");
      return;
    }

    this.exercise.set(exercise);
    this.words.set(exercise.words.map((word) => ({ ...word, matched: false })));
    this.definitions.set(exercise.definitions.map((definition) => ({ ...definition, matched: false })));
    this.side.set("words");
    this.cursor.set(0);
    this.phase.set("exercise");

    // A free answer is typed straight away — except on a phone, where focusing the field would
    // throw the keyboard up over the question before it has been read.
    if (exercise.kind !== "match" && !isTouchPrimary) {
      afterNextRender(() => this.focusAnswer(), { injector: this.#injector });
    }
  }

  #fail(error: ReviewError): void {
    this.message.set(describeError(error));
    this.phase.set("failed");
  }

  protected onEditingChange(editing: boolean): void {
    this.editing.set(editing);
  }
}
