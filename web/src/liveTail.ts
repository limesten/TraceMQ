import { useCallback, useEffect, useRef, useState } from 'react';
import { api, type MessageRow } from './api';
import { useView } from './store';

/**
 * How many rows the browser keeps. The server holds 100k in its ring and millions on disk;
 * this is only what the table can scroll through without the tab growing without bound.
 */
export const MAX_ROWS = 20_000;

export const POLL_MS = 1_000;

/**
 * Merge a freshly polled page onto the rows already held. The page is newest-first and so is
 * the result. Ids are assigned by ingest and strictly increasing, so a row already present
 * can only be a duplicate from an overlapping poll, and duplicates are dropped rather than
 * rendered twice.
 */
export function mergeRows(existing: MessageRow[], incoming: MessageRow[], cap = MAX_ROWS): MessageRow[] {
    if (incoming.length === 0) return existing;

    const seen = new Set(existing.map((row) => row.id));
    const fresh = incoming.filter((row) => !seen.has(row.id));
    if (fresh.length === 0) return existing;

    fresh.sort((a, b) => b.id - a.id);
    return [...fresh, ...existing].slice(0, cap);
}

/**
 * Delta is measured against the row below, which is the previous message in time. Rows are
 * newest-first, so the last row on screen has nothing to compare against.
 */
export function deltaFor(rows: MessageRow[], index: number): number | null {
    const below = rows[index + 1];
    return below ? rows[index].ts - below.ts : null;
}

export interface LiveTail {
    rows: MessageRow[];
    pending: MessageRow[];
    flush: () => void;
    state: 'loading' | 'ready' | 'error';
    error: string | null;
}

/**
 * The table's data. A correlation search is a look at history: it loads once and does not
 * poll. Everything else is a live tail — one query for the first page, then an incremental
 * poll by cursor, which the server answers from its ring rather than the database.
 */
export function useLiveTail(): LiveTail {
    const topicFilter = useView((s) => s.topicFilter);
    const correlation = useView((s) => s.correlationSearch.trim());
    const paused = useView((s) => s.paused);
    const autoScroll = useView((s) => s.autoScroll);

    const [rows, setRows] = useState<MessageRow[]>([]);
    const [pending, setPending] = useState<MessageRow[]>([]);
    const [state, setState] = useState<LiveTail['state']>('loading');
    const [error, setError] = useState<string | null>(null);

    const cursor = useRef(0);

    // First page, and a full reload whenever the filter changes.
    useEffect(() => {
        let cancelled = false;
        setState('loading');
        setError(null);
        setRows([]);
        setPending([]);
        cursor.current = 0;

        api.messages({
            topic: topicFilter || undefined,
            correlation: correlation || undefined,
            limit: 500,
        })
            .then((page) => {
                if (cancelled) return;
                setRows(page);
                // The page is newest-first, so the first row is the newest id seen.
                cursor.current = page.length > 0 ? page[0].id : 0;
                setState('ready');
            })
            .catch((cause: Error) => {
                if (cancelled) return;
                setError(cause.message);
                setState('error');
            });

        return () => {
            cancelled = true;
        };
    }, [topicFilter, correlation]);

    // Incremental poll. autoScroll is a dependency rather than a ref read during render:
    // toggling it restarts the interval, which costs one skipped tick and nothing else.
    useEffect(() => {
        if (correlation || paused || state !== 'ready') return;

        const timer = setInterval(async () => {
            try {
                const page = await api.messages({
                    afterId: cursor.current,
                    topic: topicFilter || undefined,
                    limit: 500,
                });
                if (page.length === 0) return;

                cursor.current = Math.max(cursor.current, page[0].id);
                // Auto scroll off means the view is being read: hold new rows aside rather
                // than moving what is under the pointer.
                const target = autoScroll ? setRows : setPending;
                target((prev) => mergeRows(prev, page));
            } catch {
                // A dropped poll is not worth surfacing; the next one is a second away.
            }
        }, POLL_MS);

        return () => clearInterval(timer);
    }, [correlation, paused, state, topicFilter, autoScroll]);

    const flush = useCallback(() => {
        setPending((held) => {
            if (held.length > 0) setRows((prev) => mergeRows(prev, held));
            return [];
        });
    }, []);

    // Turning auto scroll back on releases whatever was held.
    useEffect(() => {
        if (autoScroll) flush();
    }, [autoScroll, flush]);

    return { rows, pending, flush, state, error };
}
