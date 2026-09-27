import { describe, expect, it } from 'vitest';
import { EMPTY_RANGE, normalizeTime, resolveRange, stepTime, type RangeForm } from './timeRange';

const form = (patch: Partial<RangeForm>): RangeForm => ({ ...EMPTY_RANGE, ...patch });

// Expectations are built with local-time Date constructors, so they hold in any timezone.
const local = (y: number, m: number, d: number, h = 0, min = 0, s = 0, ms = 0) =>
    new Date(y, m - 1, d, h, min, s, ms).getTime();

describe('resolveRange', () => {
    it('is no range while no date is picked', () => {
        expect(resolveRange(EMPTY_RANGE)).toEqual({ kind: 'none' });
    });

    it('takes a single date as the whole day', () => {
        expect(resolveRange(form({ fromDate: '2026-09-27' }))).toEqual({
            kind: 'ok',
            from: local(2026, 9, 27),
            to: local(2026, 9, 27, 23, 59, 59, 999),
        });
    });

    it('narrows a single date to a window when times are on', () => {
        const range = resolveRange(
            form({ fromDate: '2026-09-27', useTime: true, fromTime: '10:15:00', toTime: '10:15:30' }),
        );

        expect(range).toEqual({
            kind: 'ok',
            from: local(2026, 9, 27, 10, 15, 0),
            to: local(2026, 9, 27, 10, 15, 30, 999),
        });
    });

    it('runs a date range from the first midnight to the end of the last day', () => {
        const range = resolveRange(form({ mode: 'range', fromDate: '2026-09-25', toDate: '2026-09-27' }));

        expect(range).toEqual({
            kind: 'ok',
            from: local(2026, 9, 25),
            to: local(2026, 9, 27, 23, 59, 59, 999),
        });
    });

    it('applies times to the first and last day of a range', () => {
        const range = resolveRange(
            form({
                mode: 'range',
                fromDate: '2026-09-25',
                toDate: '2026-09-27',
                useTime: true,
                fromTime: '22:00:00',
                toTime: '06:00:00',
            }),
        );

        expect(range).toEqual({
            kind: 'ok',
            from: local(2026, 9, 25, 22),
            to: local(2026, 9, 27, 6, 0, 0, 999),
        });
    });

    it('reads a time without seconds as zero seconds', () => {
        const range = resolveRange(
            form({ fromDate: '2026-09-27', useTime: true, fromTime: '10:15', toTime: '10:16' }),
        );

        expect(range).toMatchObject({ from: local(2026, 9, 27, 10, 15), to: local(2026, 9, 27, 10, 16, 0, 999) });
    });

    it('keeps the end of the same second when start and end are equal', () => {
        const range = resolveRange(
            form({ fromDate: '2026-09-27', useTime: true, fromTime: '10:15:30', toTime: '10:15:30' }),
        );

        expect(range).toMatchObject({ kind: 'ok' });
    });

    it('rejects an end before the start', () => {
        expect(
            resolveRange(form({ fromDate: '2026-09-27', useTime: true, fromTime: '11:00:00', toTime: '10:00:00' })).kind,
        ).toBe('invalid');
        expect(resolveRange(form({ mode: 'range', fromDate: '2026-09-27', toDate: '2026-09-26' })).kind).toBe('invalid');
    });

    it('asks for an end date in range mode', () => {
        expect(resolveRange(form({ mode: 'range', fromDate: '2026-09-27' })).kind).toBe('invalid');
    });

    it('rejects dates that do not exist rather than rolling them over', () => {
        expect(resolveRange(form({ fromDate: '2026-02-30' })).kind).toBe('invalid');
    });

    it('rejects a half-typed time', () => {
        expect(resolveRange(form({ fromDate: '2026-09-27', useTime: true, fromTime: '' })).kind).toBe('invalid');
    });
});

describe('normalizeTime', () => {
    it.each([
        ['10:15:30', '10:15:30'],
        ['9', '09:00:00'],
        ['9:5', '09:05:00'],
        ['14:30', '14:30:00'],
        ['101530', '10:15:30'],
        ['1015', '10:15:00'],
        [' 23:59:59 ', '23:59:59'],
    ])('reads %j as %j', (raw, expected) => {
        expect(normalizeTime(raw)).toBe(expected);
    });

    it.each(['24:00:00', '10:60', 'ten', '1:2:3:4', ''])('leaves %j for resolveRange to flag', (raw) => {
        expect(normalizeTime(raw)).toBe(raw);
    });
});

describe('stepTime', () => {
    it('steps the part under the caret', () => {
        expect(stepTime('10:15:30', 0, 1)).toBe('11:15:30');
        expect(stepTime('10:15:30', 1, -1)).toBe('10:14:30');
        expect(stepTime('10:15:30', 2, 1)).toBe('10:15:31');
    });

    it('wraps each part on its own', () => {
        expect(stepTime('23:59:59', 0, 1)).toBe('00:59:59');
        expect(stepTime('00:00:00', 2, -1)).toBe('00:00:59');
    });

    it('leaves a value it cannot read alone', () => {
        expect(stepTime('ten', 0, 1)).toBe('ten');
    });
});
