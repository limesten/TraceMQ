import { describe, expect, it } from 'vitest';
import type { MessageRow } from './api';
import { deltaFor, mergeRows } from './liveTail';

const row = (id: number): MessageRow => ({
    id,
    ts: 1_000 + id,
    topic: 'codeit/a',
    correlationKey: null,
    qos: 0,
    retained: false,
    size: 2,
    preview: '{"n":1}',
});

describe('mergeRows', () => {
    it('puts newer rows in front, newest first', () => {
        const existing = [row(3), row(2), row(1)];

        expect(mergeRows(existing, [row(5), row(4)]).map((r) => r.id)).toEqual([5, 4, 3, 2, 1]);
    });

    it('drops duplicates from overlapping polls', () => {
        const existing = [row(3), row(2), row(1)];

        expect(mergeRows(existing, [row(4), row(3), row(2)]).map((r) => r.id)).toEqual([4, 3, 2, 1]);
    });

    it('returns the same array when the page adds nothing', () => {
        const existing = [row(2), row(1)];

        expect(mergeRows(existing, [])).toBe(existing);
        expect(mergeRows(existing, [row(1)])).toBe(existing);
    });

    it('sorts a page that arrives out of order', () => {
        expect(mergeRows([], [row(2), row(5), row(3)]).map((r) => r.id)).toEqual([5, 3, 2]);
    });

    it('caps the buffer, keeping the newest', () => {
        const existing = [row(3), row(2), row(1)];

        expect(mergeRows(existing, [row(5), row(4)], 3).map((r) => r.id)).toEqual([5, 4, 3]);
    });

    it('starts from empty', () => {
        expect(mergeRows([], [row(2), row(1)]).map((r) => r.id)).toEqual([2, 1]);
    });
});

describe('deltaFor', () => {
    // Rows are newest-first, so a row's delta is measured against the row BELOW it, which is
    // the message before it in time.
    const rows = [
        { ...row(3), ts: 65_126 },
        { ...row(2), ts: 5_126 },
        { ...row(1), ts: 1_861 },
    ];

    it('measures against the row below', () => {
        expect(deltaFor(rows, 0)).toBe(60_000);
        expect(deltaFor(rows, 1)).toBe(3_265);
    });

    it('has no delta for the oldest row on screen', () => {
        expect(deltaFor(rows, 2)).toBeNull();
    });

    it('has no delta for a single row', () => {
        expect(deltaFor([row(1)], 0)).toBeNull();
    });

    it('is empty-safe', () => {
        expect(deltaFor([], 0)).toBeNull();
    });

    it('can be negative if a publisher clock runs backwards', () => {
        // Timestamps come from the ingesting host, but two rows can still land out of order
        // within a millisecond. formatDelta renders anything negative as no delta.
        const backwards = [{ ...row(2), ts: 100 }, { ...row(1), ts: 200 }];

        expect(deltaFor(backwards, 0)).toBe(-100);
    });
});
