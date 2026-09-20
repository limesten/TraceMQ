import { describe, expect, it } from 'vitest';
import { hexDump, toJsonLines, tryParseJson } from './json';

const render = (value: unknown) =>
    toJsonLines(value).map((l) => `${l.pad}${l.key}${l.colon}${l.value}${l.punct}`);

describe('toJsonLines', () => {
    it('renders the payload shape these services publish', () => {
        expect(
            render({
                timestamp: 1789893541902,
                trigger: { uid: '7347ba7c', topic: 'codeit/a' },
                actionStatus: { Status: 0, Info: 'Received' },
            }),
        ).toEqual([
            '{',
            '  "timestamp": 1789893541902,',
            '  "trigger": {',
            '    "uid": "7347ba7c",',
            '    "topic": "codeit/a"',
            '  },',
            '  "actionStatus": {',
            '    "Status": 0,',
            '    "Info": "Received"',
            '  }',
            '}',
        ]);
    });

    it('renders arrays of objects', () => {
        expect(render({ lines: [{ index: 1 }, { index: 2 }] })).toEqual([
            '{',
            '  "lines": [',
            '    {',
            '      "index": 1',
            '    },',
            '    {',
            '      "index": 2',
            '    }',
            '  ]',
            '}',
        ]);
    });

    it('keeps empty containers on one line', () => {
        expect(render({ a: {}, b: [] })).toEqual(['{', '  "a": {},', '  "b": []', '}']);
    });

    it('handles every scalar kind', () => {
        expect(render({ s: 'x', n: 1.5, t: true, f: false, z: null })).toEqual([
            '{',
            '  "s": "x",',
            '  "n": 1.5,',
            '  "t": true,',
            '  "f": false,',
            '  "z": null',
            '}',
        ]);
    });

    it('tags each value so the renderer can colour it', () => {
        const kinds = toJsonLines({ s: 'x', n: 1, t: true, z: null })
            .filter((l) => l.value !== '')
            .map((l) => l.valueKind);

        expect(kinds).toEqual(['string', 'number', 'boolean', 'null']);
    });

    it('escapes keys and values rather than emitting them raw', () => {
        expect(render({ 'a"b': 'c\nd' })).toEqual(['{', '  "a\\"b": "c\\nd"', '}']);
    });

    it('renders a payload that is not an object at all', () => {
        expect(render('bare')).toEqual(['"bare"']);
        expect(render(42)).toEqual(['42']);
        expect(render(null)).toEqual(['null']);
    });

    it('survives deep nesting', () => {
        let deep: unknown = 1;
        for (let i = 0; i < 200; i++) deep = { nested: deep };

        expect(() => toJsonLines(deep)).not.toThrow();
    });
});

describe('tryParseJson', () => {
    it('reports success with the parsed value', () => {
        expect(tryParseJson('{"a":1}')).toEqual({ ok: true, value: { a: 1 } });
    });

    it.each(['', 'not json', '{"a":', '{a:1}'])('reports failure for %j', (text) => {
        expect(tryParseJson(text)).toEqual({ ok: false });
    });
});

describe('hexDump', () => {
    it('shows offset, bytes and printable ascii', () => {
        expect(hexDump(new Uint8Array([0x00, 0xff, 0x41, 0x42]))).toBe(
            '00000000  00 ff 41 42                                      ..AB',
        );
    });

    it('wraps at sixteen bytes per line', () => {
        expect(hexDump(new Uint8Array(20)).split('\n')).toHaveLength(2);
    });

    it('truncates and says by how much', () => {
        const dump = hexDump(new Uint8Array(100), 32);

        expect(dump).toContain('… 68 more bytes');
    });

    it('is empty for empty input', () => {
        expect(hexDump(new Uint8Array())).toBe('');
    });
});
