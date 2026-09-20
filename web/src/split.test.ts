import { describe, expect, it } from 'vitest';
import {
    clampPaneWidth,
    DEFAULT_PAYLOAD_WIDTH,
    MIN_PAYLOAD_WIDTH,
    MIN_TABLE_WIDTH,
    RAIL_WIDTH,
} from './split';

describe('clampPaneWidth', () => {
    const wide = 1920;

    it('leaves a comfortable width alone', () => {
        expect(clampPaneWidth(700, wide)).toBe(700);
    });

    it('never goes below the payload minimum', () => {
        expect(clampPaneWidth(10, wide)).toBe(MIN_PAYLOAD_WIDTH);
        expect(clampPaneWidth(-500, wide)).toBe(MIN_PAYLOAD_WIDTH);
    });

    it('leaves the table its minimum', () => {
        expect(clampPaneWidth(99_999, wide)).toBe(wide - RAIL_WIDTH - MIN_TABLE_WIDTH);
    });

    it('still yields a usable pane in a window too narrow for both minimums', () => {
        // Pinning to a negative or zero width would collapse the pane entirely; the pane
        // keeps its minimum and the table scrolls instead.
        expect(clampPaneWidth(DEFAULT_PAYLOAD_WIDTH, 400)).toBe(MIN_PAYLOAD_WIDTH);
    });

    it('rounds to whole pixels', () => {
        expect(clampPaneWidth(700.6, wide)).toBe(701);
    });
});
