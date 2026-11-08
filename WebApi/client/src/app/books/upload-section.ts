import { ChangeDetectionStrategy, Component, type ElementRef, output, signal, viewChild } from "@angular/core";
import { appUrl } from "../core/app-base";
import type { BookUploadResult } from "../core/hub-models";
import { ProgressDots } from "../core/progress-dots";

/** The server's limit (BookUploadService.MaximumUploadBytes); checked here only to save the upload. */
const maximumUploadBytes = 64 * 1024 * 1024;

/** The formats the server reads. A new one (FB2) is a new parser there and an entry here. */
const acceptedExtensions = [".epub"];

/** What the reader is told while it happens. */
type Stage = { readonly kind: "uploading"; readonly percent: number } | { readonly kind: "reading" };

/** The server's refusal codes, in the page's words. */
function describeError(code: string | null): string {
  switch (code) {
    case "too-large":
      return "That file is over 64 MB.";
    case "empty":
      return "That file is empty.";
    case "unsupported":
      return "Only EPUB books can be uploaded for now.";
    case "unreadable":
      return "That EPUB could not be read — it may be damaged.";
    case "no-content":
      return "There is no text or picture in that book to read.";
    case "too-many-books":
      return "Your shelf is full.";
    case "rate-limited":
      return "That is a lot of uploads at once — try again in a while.";
    default:
      return "Could not upload the book — try again.";
  }
}

/**
 * Upload a book of your own (D50). The file goes to the server, which reads it into chapters and
 * pictures, keeps those, and drops the file; the book then opens at its first chapter as the
 * novel being read.
 *
 * The file is sent as the request body, not a form, so the server can read it straight into
 * memory. XHR rather than fetch, because only XHR reports how far an upload has got — a 15 MB
 * volume on a phone takes long enough to need saying.
 *
 * The file input itself sits outside every `.settings-row`, so the drawer's keyboard grid (D36)
 * steps over it and lands on the button, which opens it.
 */
@Component({
  selector: "app-upload-section",
  imports: [ProgressDots],
  templateUrl: "./upload-section.html",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UploadSection {
  /** A book is on the shelf; the page closes the drawer and opens it. */
  readonly uploaded = output<{ slug: string; title: string; chapterCount: number }>();

  protected readonly accept = [...acceptedExtensions, "application/epub+zip"].join(",");
  protected readonly stage = signal<Stage | undefined>(undefined);
  protected readonly note = signal("");

  protected readonly file = viewChild.required<ElementRef<HTMLInputElement>>("file");

  protected choose(): void {
    if (this.stage() !== undefined) return;
    this.file().nativeElement.click();
  }

  protected async picked(): Promise<void> {
    const input = this.file().nativeElement;
    const file = input.files?.[0];
    // Cleared at once, so choosing the same file again after a failure still fires `change`.
    input.value = "";
    if (!file || this.stage() !== undefined) return;

    if (!acceptedExtensions.some((extension) => file.name.toLowerCase().endsWith(extension))) {
      this.note.set(describeError("unsupported"));
      return;
    }
    if (file.size > maximumUploadBytes) {
      this.note.set(describeError("too-large"));
      return;
    }

    this.note.set("");
    this.stage.set({ kind: "uploading", percent: 0 });
    try {
      const result = await this.#send(file);
      if (!result?.succeeded || result.slug === null) {
        this.note.set(describeError(result?.error ?? null));
        return;
      }

      const title = result.title ?? file.name;
      this.note.set(`Added “${title}”.`);
      this.uploaded.emit({ slug: result.slug, title, chapterCount: result.chapterCount });
    } finally {
      this.stage.set(undefined);
    }
  }

  /** Resolves with the server's answer, or undefined when there was none to read. */
  #send(file: File): Promise<BookUploadResult | undefined> {
    return new Promise((resolve) => {
      const request = new XMLHttpRequest();
      request.open("POST", appUrl(`books?fileName=${encodeURIComponent(file.name)}`));
      request.responseType = "json";

      request.upload.onprogress = (event) => {
        if (event.lengthComputable) {
          this.stage.set({ kind: "uploading", percent: Math.floor((event.loaded / event.total) * 100) });
        }
      };
      // Sent; the server is now unpacking it, which takes a moment of its own.
      request.upload.onload = () => this.stage.set({ kind: "reading" });

      request.onload = () => {
        if (request.status === 401) {
          window.location.href = appUrl("Login");
          return;
        }
        const answer = request.response as BookUploadResult | null;
        // A proxy in front of the app refuses a large body with its own page, not JSON.
        resolve(answer ?? (request.status === 413
          ? { succeeded: false, slug: null, title: null, chapterCount: 0, error: "too-large" }
          : undefined));
      };
      request.onerror = () => resolve(undefined);
      request.onabort = () => resolve(undefined);

      request.setRequestHeader("Content-Type", "application/octet-stream");
      request.send(file);
    });
  }
}
