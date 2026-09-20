import { useQuery } from '@tanstack/react-query';
import { api, type MessageRow } from '../api';
import { formatDelta, formatTime, groupDigits, splitTopic } from '../format';
import { useView } from '../store';

/** Delta is measured against the row below, which is the previous message in time. */
export function deltaFor(rows: MessageRow[], index: number): number | null {
    const below = rows[index + 1];
    return below ? rows[index].ts - below.ts : null;
}

function Row({ row, delta, selected, onSelect }: {
    row: MessageRow;
    delta: number | null;
    selected: boolean;
    onSelect: () => void;
}) {
    const { head, tail } = splitTopic(row.topic);
    // A gap of a minute or more inside a sequence is the thing being hunted.
    const slow = delta !== null && delta >= 60_000;

    return (
        <button
            type="button"
            onClick={onSelect}
            className={`flex h-[30px] w-full items-center gap-3 border-l-2 pr-3.5 pl-3 text-left ${
                selected ? 'border-l-accent bg-selected' : 'border-l-transparent hover:bg-hover'
            }`}
        >
            <span
                className={`w-[92px] shrink-0 font-mono text-[11.5px] ${
                    selected ? 'text-ink' : 'text-ink-dim'
                }`}
            >
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
    const view = useView();

    const { data: rows = [], isLoading, error } = useQuery({
        queryKey: ['messages', view.topicFilter, view.correlationSearch],
        queryFn: () =>
            api.messages({
                topic: view.topicFilter || undefined,
                correlation: view.correlationSearch.trim() || undefined,
                limit: 500,
            }),
        refetchInterval: view.paused || view.correlationSearch ? false : 1000,
    });

    return (
        <section className="flex min-w-[260px] grow flex-col border-r border-hairline">
            <div className="flex h-[34px] shrink-0 items-center gap-3 border-b border-hairline bg-panel px-3.5 text-[10.5px] tracking-wider text-ink-faint uppercase">
                <span className="w-[92px] shrink-0">Time</span>
                <span className="w-[58px] shrink-0 text-right">Delta</span>
                <span className="grow">Topic</span>
            </div>

            <div className="grow overflow-y-auto scroll-thin">
                {isLoading && <p className="p-3.5 text-xs text-ink-faint">Loading…</p>}
                {error && (
                    <p className="p-3.5 font-mono text-xs text-warn">
                        {(error as Error).message}
                    </p>
                )}
                {!isLoading && !error && rows.length === 0 && (
                    <p className="p-3.5 text-xs text-ink-faint">
                        No messages match {view.correlationSearch ? 'that correlation key' : 'this filter'}.
                    </p>
                )}
                {rows.map((row, index) => (
                    <Row
                        key={row.id}
                        row={row}
                        delta={deltaFor(rows, index)}
                        selected={view.selectedId === row.id}
                        onSelect={() => view.select(row.id)}
                    />
                ))}
            </div>

            <div className="flex h-7 shrink-0 items-center border-t border-hairline bg-panel px-3.5 text-[11px] text-ink-faint">
                {groupDigits(rows.length)} rows
            </div>
        </section>
    );
}
