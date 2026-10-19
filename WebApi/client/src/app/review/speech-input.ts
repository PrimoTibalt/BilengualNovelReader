/**
 * Dictation through the browser's speech recognition, for answers that are easier said than
 * typed — a definition in your own words runs to a sentence or two.
 *
 * Only Chromium-based browsers and Safari have it; Firefox does not. Where it is missing the
 * mic button is not shown, and a phone's own keyboard dictation still works in the answer
 * field, since it is an ordinary text field. The microphone is allowed for this origin by the
 * Permissions-Policy (SecurityHeaders).
 */

interface RecognitionResult {
  readonly isFinal: boolean;
  readonly 0: { readonly transcript: string };
}

interface RecognitionEvent {
  readonly resultIndex: number;
  readonly results: ArrayLike<RecognitionResult>;
}

interface Recognition {
  lang: string;
  continuous: boolean;
  interimResults: boolean;
  onresult: ((event: RecognitionEvent) => void) | null;
  onend: (() => void) | null;
  onerror: ((event: { readonly error: string }) => void) | null;
  start(): void;
  stop(): void;
}

type RecognitionConstructor = new () => Recognition;

const RecognitionImplementation: RecognitionConstructor | undefined =
  (window as unknown as { SpeechRecognition?: RecognitionConstructor }).SpeechRecognition
  ?? (window as unknown as { webkitSpeechRecognition?: RecognitionConstructor }).webkitSpeechRecognition;

export const speechInputAvailable = RecognitionImplementation !== undefined;

export interface SpeechCallbacks {
  /** Everything heard so far in this take: what is settled, and the guess at what follows. */
  readonly onText: (settled: string, interim: string) => void;
  readonly onEnd: (error: string | null) => void;
}

/** One take: starts listening, and reports until it is stopped or the speaker falls silent. */
export function listen(callbacks: SpeechCallbacks): { stop(): void } | undefined {
  if (!RecognitionImplementation) return undefined;

  const recognition = new RecognitionImplementation();
  // Every answer the review asks for is English, the translation exercise's included.
  recognition.lang = "en-US";
  recognition.continuous = true;
  recognition.interimResults = true;

  let settled = "";
  let failure: string | null = null;
  recognition.onresult = (event) => {
    let interim = "";
    for (let index = event.resultIndex; index < event.results.length; index++) {
      const result = event.results[index]!;
      if (result.isFinal) settled += result[0].transcript;
      else interim += result[0].transcript;
    }
    callbacks.onText(settled.trim(), interim.trim());
  };
  recognition.onerror = (event) => {
    failure = event.error;
  };
  recognition.onend = () => callbacks.onEnd(failure);
  recognition.start();

  return { stop: () => recognition.stop() };
}
