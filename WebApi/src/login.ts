/**
 * The login screen: one panel, two fields, two modes.
 *
 * Both modes post JSON and act on the answer in place, so a rejected password does not cost
 * a page reload and the typed username survives it. The mode switch is a pair of real
 * buttons rather than a key binding — arrow keys inside a text field belong to the caret,
 * and a login screen is the wrong place to be clever about keyboard capture.
 */

type Mode = "signin" | "signup";

interface AuthResponse {
  readonly succeeded: boolean;
  readonly userName: string | null;
  readonly message: string | null;
  readonly redirectTo: string | null;
}

const form = document.getElementById("login-form") as HTMLFormElement | null;
const userNameInput = document.getElementById("username") as HTMLInputElement | null;
const passwordInput = document.getElementById("password") as HTMLInputElement | null;
const submitButton = document.getElementById("submit") as HTMLButtonElement | null;
const messageElement = document.getElementById("message");
const modeCaption = document.getElementById("mode-caption");
const signInTab = document.getElementById("tab-signin") as HTMLButtonElement | null;
const signUpTab = document.getElementById("tab-signup") as HTMLButtonElement | null;

let mode: Mode = "signin";
let submitting = false;

const captions: Record<Mode, string> = {
  signin: "sign in",
  signup: "sign up",
};

function setMessage(text: string, kind: "error" | "working" | "none" = "none"): void {
  if (!messageElement) return;

  messageElement.textContent = text;
  messageElement.classList.toggle("login-message--error", kind === "error");
  messageElement.classList.toggle("login-message--working", kind === "working");
}

function setMode(next: Mode): void {
  mode = next;

  signInTab?.setAttribute("aria-selected", String(next === "signin"));
  signUpTab?.setAttribute("aria-selected", String(next === "signup"));

  if (modeCaption) modeCaption.textContent = captions[next];
  if (submitButton) submitButton.textContent = captions[next];

  // Password managers offer to save a new password only when told this is a new one.
  if (passwordInput) {
    passwordInput.autocomplete = next === "signup" ? "new-password" : "current-password";
  }

  setMessage(next === "signup" ? "Pick a username and a password of at least 8 characters." : "");
}

function setSubmitting(value: boolean): void {
  submitting = value;
  if (submitButton) submitButton.disabled = value;
}

async function submit(): Promise<void> {
  if (submitting) return;

  const userName = userNameInput?.value.trim() ?? "";
  const password = passwordInput?.value ?? "";

  if (userName.length === 0 || password.length === 0) {
    setMessage("Both a username and a password are needed.", "error");
    (userName.length === 0 ? userNameInput : passwordInput)?.focus();
    return;
  }

  setSubmitting(true);
  setMessage(mode === "signup" ? "Creating the account…" : "Signing in…", "working");

  try {
    const response = await fetch(`/auth/${mode}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ userName, password }),
      credentials: "same-origin",
    });

    const payload = (await response.json()) as AuthResponse;

    if (response.ok && payload.succeeded) {
      setMessage("Signed in. Opening your novel…");
      window.location.href = payload.redirectTo ?? "/ReadingPage";
      return;
    }

    setMessage(payload.message ?? "That did not work.", "error");
    passwordInput?.select();
  } catch {
    // A network failure is not a rejected password; say so rather than blaming the reader.
    setMessage("Could not reach the server. Is it running?", "error");
  } finally {
    setSubmitting(false);
  }
}

form?.addEventListener("submit", (event) => {
  event.preventDefault();
  void submit();
});

signInTab?.addEventListener("click", () => setMode("signin"));
signUpTab?.addEventListener("click", () => setMode("signup"));

// Left/right move between the two mode tabs while one of them has focus.
for (const tab of [signInTab, signUpTab]) {
  tab?.addEventListener("keydown", (event) => {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;

    event.preventDefault();
    const next: Mode = event.key === "ArrowRight" ? "signup" : "signin";
    setMode(next);
    (next === "signup" ? signUpTab : signInTab)?.focus();
  });
}

setMode("signin");
userNameInput?.focus();
