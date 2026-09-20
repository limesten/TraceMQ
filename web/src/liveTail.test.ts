import { describe, expect, it } from 'vitest';
import type { MessageRow } from './api';
import { mergeRows } from './liveTail';

const row = (id: number): MessageRow => ({
    id,
    ts: 1_000 + id,
    topic: 'codeit/a',
    correlationKey: null,
    qos: 0,
    retained: false,
    size: 2,
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
