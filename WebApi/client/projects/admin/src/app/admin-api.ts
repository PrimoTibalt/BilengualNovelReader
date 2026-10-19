/** The admin panel's JSON (AdminController), as AdminResponses.cs shapes it. Times are ISO strings, UTC. */

export interface OnlineConnection {
  since: string;
  lastActive: string;
  address: string | null;
  userAgent: string | null;
}

export interface OnlineReader {
  userName: string;
  connections: OnlineConnection[];
}

export interface AuthEvent {
  at: string;
  outcome: string;
  userName: string | null;
  address: string | null;
  detail: string | null;
}

export interface SignInSummary {
  lastDay: Record<string, number>;
  lastWeek: Record<string, number>;
  recent: AuthEvent[];
}

export interface ErrorEntry {
  at: string;
  level: string;
  category: string;
  message: string;
  exception: string | null;
}

export interface ReviewStats {
  cards: number;
  new: number;
  due: number;
  learning: number;
  mature: number;
}

export interface NovelRead {
  slug: string;
  title: string | null;
  chapter: number | null;
  paragraph: number | null;
  totalChapters: number | null;
  lastReadAt: string | null;
}

export interface ReaderStats {
  userName: string;
  savedWords: number;
  savedThisWeek: number;
  lastSavedAt: string | null;
  review: ReviewStats;
  novels: NovelRead[];
}

export interface Overview {
  generatedAt: string;
  online: OnlineReader[];
  signIns: SignInSummary;
  errors: ErrorEntry[];
  readers: ReaderStats[];
}

/** Milliseconds; null where nothing ran in the slot. */
export interface TimingPoint {
  start: string;
  count: number;
  p50: number | null;
  p95: number | null;
}

export interface OperationTimes {
  operation: string;
  count: number;
  p50: number;
  p95: number;
}

export interface Timings {
  from: string;
  to: string;
  slotMinutes: number;
  filter: string;
  series: TimingPoint[];
  operations: OperationTimes[];
}

export const RANGES = ["1h", "6h", "24h", "7d", "30d"] as const;
export type Range = (typeof RANGES)[number];

/** The two groups the server knows besides an operation's own name (ResponseTimeReport). */
export const HUB_FILTER = "hub";
export const HTTP_FILTER = "http";

/** Thrown when the session has gone: the page goes to sign in rather than showing an error. */
export class SignedOut extends Error {}

/** Resolved against the `<base href>` the server wrote: `/reader/Admin/` behind nginx. */
export function adminUrl(path: string): string {
  return new URL(path, document.baseURI).toString();
}

export function getOverview(): Promise<Overview> {
  return getJson<Overview>("api/overview");
}

export function getTimings(range: Range, filter: string): Promise<Timings> {
  const query = new URLSearchParams({ range, filter });
  return getJson<Timings>(`api/timings?${query}`);
}

async function getJson<T>(path: string): Promise<T> {
  const response = await fetch(adminUrl(path), {
    credentials: "same-origin",
    headers: { Accept: "application/json" },
  });
  // 401: signed out. 404: signed in as someone who is not an admin any more.
  if (response.status === 401 || response.status === 404) throw new SignedOut();
  if (!response.ok) throw new Error(`The server answered ${response.status}.`);
  return (await response.json()) as T;
}
