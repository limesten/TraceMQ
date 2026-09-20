export type TokenKind = 'key' | 'string' | 'number' | 'boolean' | 'null' | 'punct';

export interface JsonLine {
    /** Leading indent, rendered as-is inside a `white-space: pre` element. */
    pad: string;
    key: string;
    colon: string;
    value: string;
    valueKind: TokenKind;
    punct: string;
}

/**
 * Pretty-print a parsed payload into coloured lines.
 *
 * A JSON line only ever has the shape `<pad><key><colon><value><punct>`, so the lines are
 * flat records rather than a tree, and the renderer is five spans with no recursion. A
 * highlighting library would be a much larger dependency for a narrower result.
 */
export function toJsonLines(value: unknown, indent = '  '): JsonLine[] {
    const lines: JsonLine[] = [];

    const push = (pad: string, key: string, colon: string, v: string, kind: TokenKind, punct: string) =>
        lines.push({ pad, key, colon, value: v, valueKind: kind, punct });

    const walk = (node: unknown, pad: string, key: string | null, tail: string) => {
        const label = key === null ? '' : JSON.stringify(key);
        const colon = key === null ? '' : ': ';

        if (Array.isArray(node)) {
            if (node.length === 0) {
                push(pad, label, colon, '', 'punct', `[]${tail}`);
                return;
            }
            push(pad, label, colon, '', 'punct', '[');
            node.forEach((item, i) => walk(item, pad + indent, null, i < node.length - 1 ? ',' : ''));
            push(pad, '', '', '', 'punct', `]${tail}`);
            return;
        }

        if (node !== null && typeof node === 'object') {
            const keys = Object.keys(node as Record<string, unknown>);
            if (keys.length === 0) {
                push(pad, label, colon, '', 'punct', `{}${tail}`);
                return;
            }
            push(pad, label, colon, '', 'punct', '{');
            keys.forEach((k, i) =>
                walk((node as Record<string, unknown>)[k], pad + indent, k, i < keys.length - 1 ? ',' : ''),
            );
            push(pad, '', '', '', 'punct', `}${tail}`);
            return;
        }

        if (typeof node === 'string') return push(pad, label, colon, JSON.stringify(node), 'string', tail);
        if (typeof node === 'number') return push(pad, label, colon, String(node), 'number', tail);
        if (typeof node === 'boolean') return push(pad, label, colon, String(node), 'boolean', tail);
        return push(pad, label, colon, 'null', 'null', tail);
    };

    walk(value, '', null, '');
    return lines;
}

export function tryParseJson(text: string): { ok: true; value: unknown } | { ok: false } {
    try {
        return { ok: true, value: JSON.parse(text) as unknown };
    } catch {
        return { ok: false };
    }
}

/** Canonical hex dump: offset, 16 bytes, then the printable ASCII for those bytes. */
export function hexDump(bytes: Uint8Array, maxBytes = 4096): string {
    const shown = bytes.subarray(0, maxBytes);
    const lines: string[] = [];

    for (let offset = 0; offset < shown.length; offset += 16) {
        const chunk = shown.subarray(offset, offset + 16);
        const hex = Array.from(chunk, (b) => b.toString(16).padStart(2, '0'))
            .join(' ')
            .padEnd(47, ' ');
        const ascii = Array.from(chunk, (b) => (b >= 0x20 && b < 0x7f ? String.fromCharCode(b) : '.')).join('');
        lines.push(`${offset.toString(16).padStart(8, '0')}  ${hex}  ${ascii}`);
    }

    if (bytes.length > maxBytes) {
        lines.push(`… ${bytes.length - maxBytes} more bytes`);
    }
    return lines.join('\n');
}

export function base64ToBytes(base64: string): Uint8Array {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes;
}
