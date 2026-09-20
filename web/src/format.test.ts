import { describe, expect, it } from 'vitest';
import { formatBytes, formatDelta, formatTime, groupDigits, NO_DELTA, splitTopic } from './format';

describe('formatDelta', () => {
    it.each([
        [0, '0ms'],
        [1, '1ms'],
        [842, '842ms'],
        [999, '999ms'],
        [1000, '1.0s'],
        [2254, '2.3s'],
        [4938, '4.9s'],
        [59_940, '59.9s'],
        [60_000, '1m'],
        [65_126, '1m'],
        [3_540_000, '59m'],
        [3_600_000, '1h'],
        [7_200_000, '2h'],
        [86_400_000, '1d'],
        [259_200_000, '3d'],
    ])('formats %ims as %s', (ms, expected) => {
        expect(formatDelta(ms)).toBe(expected);
    });

    // The boundaries rounding would cross if the unit were chosen first.
    it.each([
        [999.6, '1.0s'],
        [59_999, '1m'],
        [3_599_999, '1h'],
        [86_399_999, '1d'],
    ])('promotes %ims to the next unit rather than overflowing the current one', (ms, expected) => {
        expect(formatDelta(ms)).toBe(expected);
    });

    it.each([[null], [NaN], [Infinity], [-1]])('has no delta for %s', (ms) => {
        expect(formatDelta(ms as number | null)).toBe(NO_DELTA);
    });
});

describe('formatTime', () => {
    it('keeps three millisecond digits, padded', () => {
        const at = new Date(2026, 8, 20, 10, 39, 5, 42);

        expect(formatTime(at.getTime())).toBe('10:39:05.042');
    });

    it('uses a 24 hour clock', () => {
        const at = new Date(2026, 8, 20, 22, 4, 9, 7);

        expect(formatTime(at.getTime())).toBe('22:04:09.007');
    });

    it('renders midnight as 00, never 24', () => {
        const at = new Date(2026, 8, 20, 0, 0, 0, 0);

        expect(formatTime(at.getTime())).toBe('00:00:00.000');
    });
});

describe('splitTopic', () => {
    it('dims everything but the last three segments', () => {
        expect(splitTopic('codeit/boliden/DDATA/odda/foundry/fvl/bundlescan/scanner1/scanner')).toEqual({
            head: 'codeit/boliden/DDATA/odda/foundry/fvl/',
            tail: 'bundlescan/scanner1/scanner',
        });
    });

    it('leaves a short topic whole', () => {
        expect(splitTopic('codeit/a/b')).toEqual({ head: '', tail: 'codeit/a/b' });
        expect(splitTopic('codeit')).toEqual({ head: '', tail: 'codeit' });
    });
});

describe('formatBytes', () => {
    it.each([
        [0, '0 B'],
        [512, '512 B'],
        [1024, '1.0 kB'],
        [2_097_152, '2.0 MB'],
    ])('formats %i as %s', (bytes, expected) => {
        expect(formatBytes(bytes)).toBe(expected);
    });
});

describe('groupDigits', () => {
    it('groups thousands', () => {
        expect(groupDigits(1284019)).toBe('1 284 019');
        expect(groupDigits(42)).toBe('42');
    });
});
