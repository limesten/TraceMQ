/**
 * The time range filter, as the form holds it: date and time strings straight from the
 * native inputs, in the viewer's own timezone — the same one the Time column is shown in.
 */
export interface RangeForm {
    /** A range has two dates; a single date is one day, or a window inside it. */
    mode: 'day' | 'range';
    /** yyyy-mm-dd, or '' for no range at all. */
    fromDate: string;
    toDate: string;
    /** Off means whole days: 00:00:00 on the first to 23:59:59.999 on the last. */
    useTime: boolean;
    /** hh:mm:ss, or hh:mm when the browser drops zero seconds. */
    fromTime: string;
    toTime: string;
}

export const EMPTY_RANGE: RangeForm = {
    mode: 'day',
    fromDate: '',
    toDate: '',
    useTime: false,
    fromTime: '00:00:00',
    toTime: '23:59:59',
};

export type ResolvedRange =
    | { kind: 'none' }
    | { kind: 'ok'; from: number; to: number }
    | { kind: 'invalid'; reason: string };

const DATE = /^(\d{4})-(\d{2})-(\d{2})$/;
const TIME = /^(\d{2}):(\d{2})(?::(\d{2}))?$/;

/** Local midnight plus a time of day, or null when either part does not parse. */
function localMs(date: string, time: string): number | null {
    const d = DATE.exec(date);
    const t = TIME.exec(time);
    if (!d || !t) return null;

    const [year, month, day] = [Number(d[1]), Number(d[2]), Number(d[3])];
    const [hour, minute, second] = [Number(t[1]), Number(t[2]), Number(t[3] ?? 0)];
    if (hour > 23 || minute > 59 || second > 59) return null;

    const value = new Date(year, month - 1, day, hour, minute, second);
    // new Date rolls 2026-02-30 over into March rather than failing.
    if (value.getFullYear() !== year || value.getMonth() !== month - 1 || value.getDate() !== day) {
        return null;
    }
    return value.getTime();
}

/**
 * Epoch milliseconds for the server's `from` and `to`, both inclusive. The end is widened
 * to the last millisecond of its second: messages carry millisecond timestamps, and "until
 * 10:15:30" has to include one logged at 10:15:30.742.
 */
export function resolveRange(form: RangeForm): ResolvedRange {
    if (!form.fromDate) return { kind: 'none' };

    const toDate = form.mode === 'day' ? form.fromDate : form.toDate;
    if (!toDate) return { kind: 'invalid', reason: 'Pick an end date.' };

    const fromTime = form.useTime ? form.fromTime : '00:00:00';
    const toTime = form.useTime ? form.toTime : '23:59:59';

    const from = localMs(form.fromDate, fromTime);
    const toStart = localMs(toDate, toTime);
    if (from === null || toStart === null) return { kind: 'invalid', reason: 'Not a valid date or time.' };

    const to = toStart + 999;
    if (to < from) return { kind: 'invalid', reason: 'The end is before the start.' };

    return { kind: 'ok', from, to };
}

/**
 * What a time field holds after it loses focus: always hh:mm:ss, 24-hour. Loose input is
 * padded rather than rejected — "9" is 09:00:00, "9:5" is 09:05:00, "101530" is 10:15:30 —
 * and anything that still is not a time is left as typed, for resolveRange to flag.
 */
export function normalizeTime(raw: string): string {
    const text = raw.trim();
    const parts = /^\d{1,6}$/.test(text) && !text.includes(':')
        ? (text.length <= 2 ? [text] : text.match(/\d{1,2}/g) ?? [])
        : text.split(':');

    if (parts.length === 0 || parts.length > 3 || parts.some((p) => !/^\d{1,2}$/.test(p))) return raw;

    const [h, m = '0', s = '0'] = parts;
    const padded = [h, m, s].map((p) => p.padStart(2, '0'));
    if (Number(padded[0]) > 23 || Number(padded[1]) > 59 || Number(padded[2]) > 59) return raw;
    return padded.join(':');
}

/**
 * Arrow-key stepping on one part of hh:mm:ss: 0 hours, 1 minutes, 2 seconds. Each part
 * wraps on its own, the way a native time field does, rather than carrying into the next.
 */
export function stepTime(value: string, part: 0 | 1 | 2, delta: number): string {
    const parsed = TIME.exec(normalizeTime(value));
    if (!parsed) return value;

    const units = [Number(parsed[1]), Number(parsed[2]), Number(parsed[3] ?? 0)];
    const size = part === 0 ? 24 : 60;
    units[part] = (((units[part] + delta) % size) + size) % size;
    return units.map((u) => String(u).padStart(2, '0')).join(':');
}
