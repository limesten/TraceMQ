import { useVirtualizer } from '@tanstack/react-virtual';
import { useEffect, useRef } from 'react';
import type { MessageRow } from '../api';
import { formatDelta, formatTime, groupDigits, splitTopic } from '../format';
import { deltaFor, useLiveTail } from '../liveTail';
import { useView } from '../store';

const ROW_HEIGHT = 30;

function Row({ row, delta, selected, onSelect }: {
    row: MessageRow;
    delta: number | null;
    selected: boolean;
    onSelect: () => void;
}) {
    const { head, tail } = splitTopic(row.topic);
    // A gap of a minute or more is the thing being hunted.
    const slow = delta !== null && delta >= 60_000;

    return (
        <button
            type="button"
            onClick={onSelect}
            style={{ height: ROW_HEIGHT }}
            className={`flex w-full items-center gap-3 border-l-2 pr-3.5 pl-3 text-left ${
                selected ? 'border-l-accent bg-selected' : 'border-l-transparent hover:bg-hover'
            }`}
        >
            <span className={`w-[92px] shrink-0 font-mono text-[11.5px] ${selected ? 'text-ink' : 'text-ink-dim'}`}>
                {formatTime(row.ts)}
            </span>
            <span
                className={`w-[58px] shrink-0 text-right font-mono text-[11.5px] ${
                    slow ? 'text-warn' : selected ? 'text-ink' : 'text-ink-faint'
                }`}
            >
                {formatDelta(delta)}
            </span>
            <span className="min-w-0 grow truncate font-mono text-[11.5px]">
                <span className="text-topic-head">{head}</span>
                <span className={selected ? 'text-ink' : 'text-topic'}>{tail}</span>
            </span>
        </button>
    );
}

export function MessageTable() {
    const { selectedId, select, autoScroll, toggleAutoScroll, correlationSearch } = useView();
    const { rows, pending, flush, state, error } = useLiveTail();

    const scrollRef = useRef<HTMLDivElement>(null);

    const virtualizer = useVirtualizer({
        count: rows.length,
        getScrollElement: () => scrollRef.current,
        estimateSize: () => ROW_HEIGHT,
        overscan: 12,
    });

    // Auto scroll means pinned at offset 0. Rows only prepend while it is on, so holding the
    // top is enough — there is no scroll offset to compensate, which is the whole reason for
    // the pending buffer.
    useEffect(() => {
        if (autoScroll && scrollRef.current) {
            scrollRef.current.scrollTop = 0;
        }
    }, [autoScroll, rows]);

    // Scrolling away from the top disengages auto scroll; returning to it re-engages.
    const onScroll = () => {
        const top = scrollRef.current?.scrollTop ?? 0;
        if (top > 4 && autoScroll) toggleAutoScroll();
        else if (top === 0 && !autoScroll) toggleAutoScroll();
    };

    const items = virtualizer.getVirtualItems();

    return (
        <section className="flex min-w-[260px] grow flex-col border-r border-hairline">
            <div className="flex h-[34px] shrink-0 items-center gap-3 border-b border-hairline bg-panel px-3.5 text-[10.5px] tracking-wider text-ink-faint uppercase">
                <span className="w-[92px] shrink-0">Time</span>
                <span className="w-[58px] shrink-0 text-right">Delta</span>
                <span className="grow">Topic</span>
            </div>

            {pending.length > 0 && (
                <button
                    type="button"
                    // Showing what was held also resumes following. Clicking "show me the new
                    // messages" and then immediately accumulating a fresh backlog reads as the
                    // button not having worked.
                    onClick={() => {
                        flush();
                        if (!autoScroll) toggleAutoScroll();
                    }}
                    className="h-7 shrink-0 border-b border-hairline bg-accent-fill text-[11.5px] font-medium text-accent hover:brightness-125"
                >
                    {groupDigits(pending.length)} new {pending.length === 1 ? 'message' : 'messages'}, click to show
                </button>
            )}

            <div ref={scrollRef} onScroll={onScroll} className="grow overflow-y-auto scroll-thin">
                {state === 'loading' && <p className="p-3.5 text-xs text-ink-faint">Loading…</p>}
                {state === 'error' && <p className="p-3.5 font-mono text-xs text-warn">{error}</p>}
                {state === 'ready' && rows.length === 0 && (
                    <p className="p-3.5 text-xs text-ink-faint">
                        No messages match {correlationSearch ? 'that correlation key' : 'this filter'}.
                    </p>
                )}
                {rows.length > 0 && (
                    <div style={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
                        <div
                            style={{
                                position: 'absolute',
                                top: 0,
                                left: 0,
                                width: '100%',
                                transform: `translateY(${items[0]?.start ?? 0}px)`,
                            }}
                        >
                            {items.map((item) => (
                                <Row
                                    key={rows[item.index].id}
                                    row={rows[item.index]}
                                    delta={deltaFor(rows, item.index)}
                                    selected={selectedId === rows[item.index].id}
                                    onSelect={() => select(rows[item.index].id)}
                                />
                            ))}
                        </div>
                    </div>
                )}
            </div>

            <div className="flex h-7 shrink-0 items-center border-t border-hairline bg-panel px-3.5 text-[11px] text-ink-faint">
                {groupDigits(rows.length)} rows
            </div>
        </section>
    );
}
