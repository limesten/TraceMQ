import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api, type MessageRow } from './api';
import { useView } from './store';
import { resolveRange, type ResolvedRange } from './timeRange';

/**
 * How many rows the browser keeps. The server holds 100k in its ring and millions on disk;
 * this is only what the table can scroll through without the tab growing without bound.
 */
export const MAX_ROWS = 20_000;

export const POLL_MS = 1_000;

export const PAGE_SIZE = 500;

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
 * Append an older page below the rows already held. Both are newest-first. Only rows older
 * than the last one held are taken, so a page that overlaps is harmless. The cap keeps the
 * newest rows, which is why paging stops once it is reached rather than churning.
 */
export function appendOlder(existing: MessageRow[], older: MessageRow[], cap = MAX_ROWS): MessageRow[] {
    const oldest = existing.length > 0 ? existing[existing.length - 1].id : Infinity;
    const fresh = older.filter((row) => row.id < oldest).sort((a, b) => b.id - a.id);
    if (fresh.length === 0) return existing;
    return [...existing, ...fresh].slice(0, cap);
}

/** The time range filter, parsed. Memoised on the form, so it is stable between renders. */
export function useResolvedRange(): ResolvedRange {
    const form = useView((s) => s.range);
    return useMemo(() => resolveRange(form), [form]);
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
    /** Whether the server may hold older rows than the last one loaded. */
    hasMore: boolean;
    loadOlder: () => void;
    state: 'loading' | 'ready' | 'error';
    error: string | null;
}

/**
 * The table's data. A correlation search or a time range is a look at history: it loads once
 * and does not poll, and scrolling to the bottom pages further back. Everything else is a live tail — one query for the first page, then an incremental
 * poll by cursor, which the server answers from its ring rather than the database.
 */
export function useLiveTail(): LiveTail {
    const topicFilter = useView((s) => s.topicFilter);
    const correlation = useView((s) => s.correlationSearch.trim());
    const paused = useView((s) => s.paused);
    const autoScroll = useView((s) => s.autoScroll);
    const range = useResolvedRange();

    // Numbers rather than the range object, so the effects below depend on values.
    const rangeOk = range.kind === 'ok';
    const from = rangeOk ? range.from : undefined;
    const to = rangeOk ? range.to : undefined;
    // A range still being typed is left alone: what is on screen stays until it resolves,
    // rather than flipping back to the live tail between keystrokes.
    const rangeInvalid = range.kind === 'invalid';
    const history = Boolean(correlation) || rangeOk;

    const [rows, setRows] = useState<MessageRow[]>([]);
    const [pending, setPending] = useState<MessageRow[]>([]);
    const [state, setState] = useState<LiveTail['state']>('loading');
    const [error, setError] = useState<string | null>(null);
    const [hasMore, setHasMore] = useState(false);

    const cursor = useRef(0);
    // Bumped on every reload, so an older page that lands after the filter changed is dropped.
    const generation = useRef(0);
    const loadingOlder = useRef(false);

    // First page, and a full reload whenever the filter changes.
    useEffect(() => {
        if (rangeInvalid) return;

        let cancelled = false;
        generation.current += 1;
        loadingOlder.current = false;
        setState('loading');
        setError(null);
        setRows([]);
        setPending([]);
        setHasMore(false);
        cursor.current = 0;

        api.messages({
            topic: topicFilter || undefined,
            correlation: correlation || undefined,
            from,
            to,
            limit: PAGE_SIZE,
        })
            .then((page) => {
                if (cancelled) return;
                setRows(page);
                setHasMore(page.length === PAGE_SIZE);
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
    }, [topicFilter, correlation, from, to, rangeInvalid]);

    // Incremental poll. autoScroll is a dependency rather than a ref read during render:
    // toggling it restarts the interval, which costs one skipped tick and nothing else.
    useEffect(() => {
        if (history || rangeInvalid || paused || state !== 'ready') return;

        const timer = setInterval(async () => {
            try {
                const page = await api.messages({
                    afterId: cursor.current,
                    topic: topicFilter || undefined,
                    limit: PAGE_SIZE,
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
    }, [history, rangeInvalid, paused, state, topicFilter, autoScroll]);

    const loadOlder = useCallback(() => {
        if (loadingOlder.current || !hasMore || state !== 'ready' || rows.length === 0) return;
        if (rows.length >= MAX_ROWS) {
            setHasMore(false);
            return;
        }

        loadingOlder.current = true;
        const started = generation.current;
        api.messages({
            beforeId: rows[rows.length - 1].id,
            topic: topicFilter || undefined,
            correlation: correlation || undefined,
            from,
            to,
            limit: PAGE_SIZE,
        })
            .then((page) => {
                if (generation.current !== started) return;
                setRows((prev) => appendOlder(prev, page));
                setHasMore(page.length === PAGE_SIZE);
            })
            .catch(() => {
                // Left to retry: the next scroll to the bottom asks again.
            })
            .finally(() => {
                if (generation.current === started) loadingOlder.current = false;
            });
    }, [hasMore, state, rows, topicFilter, correlation, from, to]);

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

    return { rows, pending, flush, hasMore, loadOlder, state, error };
}
