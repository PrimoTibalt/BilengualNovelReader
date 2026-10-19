/** "in 10 min", "in 3 days" — when a word comes back, said the way a person would. */
export function relativeTime(isoUtc: string, now: number = Date.now()): string {
  const minutes = Math.max(0, Math.round((Date.parse(isoUtc) - now) / 60_000));
  if (minutes < 1) return "now";
  if (minutes < 60) return `in ${minutes} min`;

  const hours = Math.round(minutes / 60);
  if (hours < 24) return `in ${hours} h`;

  const days = Math.round(hours / 24);
  if (days < 60) return days === 1 ? "tomorrow" : `in ${days} days`;

  const months = Math.round(days / 30);
  return months < 24 ? `in ${months} months` : `in ${Math.round(months / 12)} years`;
}
