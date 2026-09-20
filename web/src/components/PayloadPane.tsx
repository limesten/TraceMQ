import { useQuery } from '@tanstack/react-query';
import { api } from '../api';
import { formatBytes, formatTime } from '../format';
import { useView } from '../store';

export function PayloadPane() {
    const selectedId = useView((s) => s.selectedId);

    const { data: message, isLoading } = useQuery({
        queryKey: ['message', selectedId],
        queryFn: () => api.message(selectedId as number),
        enabled: selectedId !== null,
    });

    if (selectedId === null) {
        return (
            <section className="flex w-[42%] min-w-[340px] max-w-[760px] shrink-0 items-center justify-center bg-ground">
                <p className="text-xs text-ink-faint">Select a message to see its payload.</p>
            </section>
        );
    }

    return (
        <section className="flex w-[42%] min-w-[340px] max-w-[760px] shrink-0 flex-col bg-ground">
            <div className="flex shrink-0 flex-col gap-1.5 border-b border-hairline bg-panel px-[18px] py-3">
                <div className="flex items-center gap-2.5">
                    <span className="font-mono text-xs text-ink">
                        {message ? formatTime(message.ts) : '—'}
                    </span>
                    {message && (
                        <span className="rounded bg-hover px-[7px] py-0.5 text-[10.5px] text-ink-dim">
                            {formatBytes(message.size)} · {message.encoding} · QoS {message.qos}
                        </span>
                    )}
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
                {/* Syntax-coloured pretty printing lands in Phase 7. */}
                {message && <pre className="whitespace-pre-wrap text-topic">{message.text}</pre>}
            </div>
        </section>
    );
}
