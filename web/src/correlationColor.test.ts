import { beforeEach, describe, expect, it } from 'vitest';
import { keyColor, resetKeyColors, shortKey } from './correlationColor';

describe('shortKey', () => {
    it('keeps the last four characters of a GUID', () => {
        expect(shortKey('3f2b8c1e-9a4d-4e7b-b6c2-1d5e8f0a7c3b')).toBe('7c3b');
    });

    it('leaves a short key whole', () => {
        expect(shortKey('ab1')).toBe('ab1');
    });
});

describe('keyColor', () => {
    beforeEach(resetKeyColors);

    const hue = (color: string) => Number(/(\d+)\)$/.exec(color)![1]);

    it('gives the same key the same colour every time', () => {
        const first = keyColor('uid-57');
        keyColor('uid-58');

        expect(keyColor('uid-57')).toBe(first);
    });

    it('ignores case and surrounding whitespace, as the key match does', () => {
        expect(keyColor('3F2B8C1E-9A4D')).toBe(keyColor(' 3f2b8c1e-9a4d '));
    });

    it('gives twelve keys in a row twelve different colours', () => {
        const colours = new Set(Array.from({ length: 12 }, (_, i) => keyColor(`uid-${i}`)));

        expect(colours.size).toBe(12);
    });

    it('puts keys seen one after another far apart on the wheel', () => {
        const hues = Array.from({ length: 24 }, (_, i) => hue(keyColor(`uid-${i}`)));

        for (let i = 1; i < hues.length; i++) {
            const apart = Math.abs(hues[i] - hues[i - 1]);
            expect(Math.min(apart, 360 - apart)).toBeGreaterThanOrEqual(90);
        }
    });
});
