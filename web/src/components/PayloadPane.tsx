import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api, type MessageDetail } from '../api';
import { formatBytes, formatTime } from '../format';
import { base64ToBytes, hexDump, toJsonLines, tryParseJson } from '../json';
import { useView } from '../store';

/**
 * Above this, pretty-printing is done on request. Parsing and laying out a megabyte of JSON
 * blocks the frame, and the payloads that big are usually being checked for size or for one
 * field near the top, not read end to end.
 */
const AUTO_FORMAT_LIMIT = 200 * 1024;

const kindClass: Record<string, string> = {
    key: 'text-json-key',
    string: 'text-json-string',
    number: 'text-json-number',
    boolean: 'text-json-boolean',
    null: 'text-json-null',
    punct: 'text-json-punct',
};

function JsonView({ value }: { value: unknown }) {
    return (
        <div className="whitespace-pre">
            {toJsonLines(value).map((line, i) => (
                <div key={i}>
                    <span>{line.pad}</span>
                    <span className="text-json-key">{line.key}</span>
                    <span className="text-json-punct">{line.colon}</span>
                    <span className={kindClass[line.valueKind]}>{line.value}</span>
                    <span className="text-json-punct">{line.punct}</span>
                </div>
            ))}
        </div>
    );
}

// Keyed on the message id by its caller, so selecting a different message remounts it and
// the "format anyway" decision starts fresh without an effect resetting it.
function CopyButton({ text }: { text: string }) {
    const [copied, setCopied] = useState(false);

    return (
        <button
            type="button"
            onClick={() => {
                void navigator.clipboard.writeText(text).then(() => setCopied(true));
            }}
            className="rounded border border-control px-2.5 py-0.5 text-[11px] text-ink-dim hover:bg-hover hover:text-ink"
        >
            {copied ? 'Copied' : 'Copy'}
        </button>
    );
}

function Body({ message }: { message: MessageDetail }) {
    const [forceFormat, setForceFormat] = useState(false);

    if (message.encoding === 'base64') {
        return (
            <pre className="whitespace-pre text-topic">{hexDump(base64ToBytes(message.text))}</pre>
        );
    }

    const tooBig = message.size > AUTO_FORMAT_LIMIT && !forceFormat;
    const parsed = tooBig ? { ok: false as const } : tryParseJson(message.text);

    if (parsed.ok) return <JsonView value={parsed.value} />;

    return (
        <>
            {tooBig && (
                <button
                    type="button"
                    onClick={() => setForceFormat(true)}
                    className="mb-3 rounded border border-control px-2.5 py-1 text-[11px] text-ink-dim hover:bg-hover hover:text-ink"
                >
                    Format anyway ({formatBytes(message.size)})
                </button>
            )}
            <pre className="whitespace-pre-wrap text-topic">{message.text}</pre>
        </>
    );
}

export function PayloadPane() {
    const selectedId = useView((s) => s.selectedId);

    const { data: message, isLoading } = useQuery({
        queryKey: ['message', selectedId],
        queryFn: () => api.message(selectedId as number),
        enabled: selectedId !== null,
        staleTime: Infinity,
    });

    const paneClass = 'flex w-[42%] min-w-[340px] max-w-[760px] shrink-0 flex-col bg-ground';

    if (selectedId === null) {
        return (
            <section className={`${paneClass} items-center justify-center`}>
                <p className="text-xs text-ink-faint">Select a message to see its payload.</p>
            </section>
        );
    }

    return (
        <section className={paneClass}>
            <div className="flex shrink-0 flex-col gap-1.5 border-b border-hairline bg-panel px-[18px] py-3">
                <div className="flex items-center gap-2.5">
                    <span className="font-mono text-xs text-ink">
                        {message ? formatTime(message.ts) : '—'}
                    </span>
                    {message && (
                        <span className="rounded bg-hover px-[7px] py-0.5 text-[10.5px] text-ink-dim">
                            {formatBytes(message.size)} · {message.encoding} · QoS {message.qos}
                            {message.retained && ' · retained'}
                        </span>
                    )}
                    <span className="grow" />
                    {message && <CopyButton key={message.id} text={message.text} />}
                </div>
                <span className="font-mono text-[11px] leading-normal break-all text-ink-dim">
                    {message?.topic ?? ''}
                </span>
                <div className="flex items-center gap-2">
                    <span className="text-[10.5px] text-ink-faint">correlation key</span>
                    <span
                        className={`font-mono text-[10.5px] ${
                            message?.correlationKey ? 'text-accent' : 'text-ink-faint'
                        }`}
                    >
                        {message?.correlationKey ?? 'none (no configured path resolved)'}
                    </span>
                </div>
            </div>

            <div className="grow overflow-auto px-[18px] py-4 font-mono text-xs leading-relaxed scroll-thin">
                {isLoading && <p className="text-ink-faint">Loading…</p>}
                {message && <Body key={message.id} message={message} />}
            </div>
        </section>
    );
}
