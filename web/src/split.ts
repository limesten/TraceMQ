/** The controls rail is fixed; these are the two panes either side of the handle. */
export const RAIL_WIDTH = 264;
export const MIN_PAYLOAD_WIDTH = 320;
export const MIN_TABLE_WIDTH = 300;
export const DEFAULT_PAYLOAD_WIDTH = 620;

const STORAGE_KEY = 'tracemq.payloadWidth';

/**
 * Keep the payload pane within what the window can actually give it, leaving the table
 * enough to still show a topic. Dragging past either end pins rather than fights.
 */
export function clampPaneWidth(desired: number, viewportWidth: number): number {
    const available = viewportWidth - RAIL_WIDTH - MIN_TABLE_WIDTH;
    const max = Math.max(MIN_PAYLOAD_WIDTH, available);
    return Math.round(Math.min(Math.max(desired, MIN_PAYLOAD_WIDTH), max));
}

/**
 * Remembered per browser. A pane width is a convenience, not state anything depends on, so
 * a blocked or cleared store just means the default — never an error.
 */
export function loadPaneWidth(): number {
    try {
        const stored = Number(localStorage.getItem(STORAGE_KEY));
        return Number.isFinite(stored) && stored > 0 ? stored : DEFAULT_PAYLOAD_WIDTH;
    } catch {
        return DEFAULT_PAYLOAD_WIDTH;
    }
}

export function savePaneWidth(width: number): void {
    try {
        localStorage.setItem(STORAGE_KEY, String(width));
    } catch {
        // Private windows and blocked site data. The layout still works for this session.
    }
}
