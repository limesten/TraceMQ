/**
 * The tail of a correlation key, which is what tells GUIDs apart at a glance. The full key
 * is still one hover away.
 */
export function shortKey(key: string, length = 4): string {
    return key.length <= length ? key : key.slice(-length);
}

/**
 * Twelve hues 30° apart, ordered so each is 150° from the one before it. Keys take them in
 * turn as they are first seen, so sequences that interleave in the stream — which are the
 * ones that have to be told apart — always land far apart on the wheel.
 */
const HUES = [0, 150, 300, 90, 240, 30, 180, 330, 120, 270, 60, 210];

/** Past this many keys the table has long since scrolled them away; start over. */
const MAX_REMEMBERED = 5_000;

const assigned = new Map<string, number>();

/**
 * A colour per key, so the messages of one sequence read as a group. Handed out in order
 * of first sight rather than hashed from the key: a hash puts two keys on nearly the same
 * hue as often as not, and six sequences in flight at once would regularly show two in one
 * colour. Order of first sight is per page load, which is all grouping on screen needs.
 *
 * Lightness and chroma are fixed at a level that stays legible on the dark ground. The key
 * is lower-cased first because the database matches keys case-insensitively, and the same
 * key must not show in two colours.
 */
export function keyColor(key: string): string {
    const id = key.trim().toLowerCase();

    let index = assigned.get(id);
    if (index === undefined) {
        if (assigned.size >= MAX_REMEMBERED) assigned.clear();
        index = assigned.size;
        assigned.set(id, index);
    }
    return `oklch(0.78 0.13 ${HUES[index % HUES.length]})`;
}

/** For tests: forget every assignment. */
export function resetKeyColors(): void {
    assigned.clear();
}
