/** How the panel writes times, durations and devices. Everything is shown in the browser's time zone. */

export function duration(milliseconds: number | null): string {
  if (milliseconds === null) return "–";
  if (milliseconds < 1) return `${milliseconds.toFixed(1)} ms`;
  if (milliseconds < 1000) return `${Math.round(milliseconds)} ms`;
  if (milliseconds < 10_000) return `${(milliseconds / 1000).toFixed(2)} s`;
  return `${(milliseconds / 1000).toFixed(1)} s`;
}

const clockFormat = new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit" });
const dayFormat = new Intl.DateTimeFormat(undefined, { day: "numeric", month: "short" });
const secondsFormat = new Intl.DateTimeFormat(undefined, {
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
});

export function clock(iso: string): string {
  return clockFormat.format(new Date(iso));
}

export function clockWithSeconds(iso: string): string {
  return secondsFormat.format(new Date(iso));
}

/** `14:05` today, `18 Oct 14:05` before. */
export function moment(iso: string, now: Date = new Date()): string {
  const date = new Date(iso);
  return date.toDateString() === now.toDateString()
    ? clockFormat.format(date)
    : `${dayFormat.format(date)} ${clockFormat.format(date)}`;
}

export function day(iso: string): string {
  return dayFormat.format(new Date(iso));
}

export function ago(iso: string | null, now: Date = new Date()): string {
  if (iso === null) return "never";
  const seconds = Math.max(0, (now.getTime() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return "just now";
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `${hours} h ago`;
  return `${Math.floor(hours / 24)} days ago`;
}

/** Enough of a user agent to tell the devices apart: `phone · Firefox`. */
export function device(userAgent: string | null): string {
  if (!userAgent) return "unknown";
  const kind = /iPad|Tablet/i.test(userAgent)
    ? "tablet"
    : /Mobile|Android|iPhone/i.test(userAgent)
      ? "phone"
      : "desktop";
  const browser = /Firefox\//.test(userAgent)
    ? "Firefox"
    : /Edg\//.test(userAgent)
      ? "Edge"
      : /Chrom(e|ium)\//.test(userAgent)
        ? "Chrome"
        : /Safari\//.test(userAgent)
          ? "Safari"
          : "other";
  return `${kind} · ${browser}`;
}

/** What each sign-in outcome code means, and whether it is one to look at twice. */
export const OUTCOMES: Record<string, { label: string; bad: boolean }> = {
  "signed-in": { label: "signed in", bad: false },
  "signed-up": { label: "signed up", bad: false },
  "sign-in-failed": { label: "wrong name or password", bad: true },
  "sign-in-locked": { label: "locked out", bad: true },
  "rate-limited": { label: "rate-limited", bad: true },
  "sign-up-refused": { label: "sign-up refused", bad: true },
  "password-change-refused": { label: "password change: wrong current password", bad: true },
  "password-change-locked": { label: "password change: locked out", bad: true },
};

export function outcomeLabel(code: string): string {
  return OUTCOMES[code]?.label ?? code;
}

export function isBadOutcome(code: string): boolean {
  return OUTCOMES[code]?.bad ?? true;
}
