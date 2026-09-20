/** 10:39:05.126 — hour, minute, second, millisecond, in the viewer's own timezone. */
const timeFormat = new Intl.DateTimeFormat('en-GB', {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    fractionalSecondDigits: 3,
    hour12: false,
});

export function formatTime(epochMs: number): string {
    return timeFormat.format(new Date(epochMs));
}

export const NO_DELTA = '—';

/**
 * Time since the prior message.
 *
 * Rounding happens before the unit is chosen, not after. Picking the unit first is wrong at
 * every boundary: 59 999 ms is below the 60 000 cutoff, so it would render as "60.0s" rather
 * than "1m", and 999.6 ms as "1000ms" rather than "1.0s".
 */
export function formatDelta(ms: number | null): string {
    if (ms === null || !Number.isFinite(ms) || ms < 0) return NO_DELTA;

    if (Math.round(ms) < 1000) return `${Math.round(ms)}ms`;

    const seconds = Math.round(ms / 100) / 10;
    if (seconds < 60) return `${seconds.toFixed(1)}s`;

    const minutes = Math.round(ms / 60_000);
    if (minutes < 60) return `${minutes}m`;

    const hours = Math.round(ms / 3_600_000);
    if (hours < 24) return `${hours}h`;

    return `${Math.round(ms / 86_400_000)}d`;
}

/**
 * The shared prefix is dimmed so the identifying tail reads first. Without this the part of
 * the topic that actually differs is off the right edge of the column.
 */
export function splitTopic(topic: string): { head: string; tail: string } {
    const parts = topic.split('/');
    if (parts.length <= 3) return { head: '', tail: topic };
    return {
        head: `${parts.slice(0, -3).join('/')}/`,
        tail: parts.slice(-3).join('/'),
    };
}

export function formatBytes(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} kB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/** Thin spaces, so long row counts stay readable without looking like a locale decision. */
export function groupDigits(n: number): string {
    return String(n).replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
}
