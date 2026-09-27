import { useLayoutEffect, useRef, type KeyboardEvent } from 'react';
import { useResolvedRange } from '../liveTail';
import { useView } from '../store';
import { normalizeTime, stepTime } from '../timeRange';

/** yyyy-mm-dd for today, local, which is what a date input holds. */
function today(): string {
    const d = new Date();
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

const inputClass =
    'h-8 min-w-0 rounded-md border bg-ground font-mono text-xs text-ink disabled:opacity-50';

function Segment({ active, onClick, children }: { active: boolean; onClick: () => void; children: string }) {
    return (
        <button
            type="button"
            aria-pressed={active}
            onClick={onClick}
            className={`h-6 px-2.5 text-[11px] ${
                active ? 'bg-accent-fill font-medium text-accent' : 'text-ink-dim hover:bg-hover hover:text-ink'
            }`}
        >
            {children}
        </button>
    );
}

/**
 * A 24-hour hh:mm:ss field. Not <input type="time">: that one takes its 12- or 24-hour
 * display from the operating system's locale, and no attribute on the page overrides it.
 * Typing is free-form and tidied on blur; up and down step the part under the caret.
 */
function TimeField({ value, onChange, label, className }: {
    value: string;
    onChange: (value: string) => void;
    label: string;
    className: string;
}) {
    const ref = useRef<HTMLInputElement>(null);
    const stepped = useRef<0 | 1 | 2 | null>(null);

    // Re-rendering a controlled input with a new value puts the caret at the end. Select the
    // stepped part again before the browser handles the next key, so holding an arrow keeps
    // stepping the same part rather than sliding onto the seconds.
    useLayoutEffect(() => {
        const part = stepped.current;
        if (part === null || !ref.current) return;
        stepped.current = null;
        ref.current.setSelectionRange(part * 3, part * 3 + 2);
    }, [value]);

    const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
        if (e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return;
        e.preventDefault();

        const caret = e.currentTarget.selectionStart ?? 0;
        const part = caret <= 2 ? 0 : caret <= 5 ? 1 : 2;
        stepped.current = part;
        onChange(stepTime(value, part, e.key === 'ArrowUp' ? 1 : -1));
    };

    return (
        <input
            ref={ref}
            type="text"
            inputMode="numeric"
            spellCheck={false}
            maxLength={8}
            placeholder="hh:mm:ss"
            aria-label={label}
            value={value}
            onChange={(e) => onChange(e.target.value)}
            onBlur={() => onChange(normalizeTime(value))}
            onKeyDown={onKeyDown}
            className={className}
        />
    );
}

/**
 * The time range filter: one day, or a span of days, each optionally narrowed to a time of
 * day down to the second. Native inputs, in the viewer's own timezone. A range that
 * resolves applies at once, the same as the topic filter; one that does not says why and
 * leaves the table as it was.
 */
export function TimeRangeControl() {
    const form = useView((s) => s.range);
    const setRange = useView((s) => s.setRange);
    const clearRange = useView((s) => s.clearRange);
    const resolved = useResolvedRange();

    const active = resolved.kind === 'ok';
    const border = resolved.kind === 'invalid' ? 'border-warn' : active ? 'border-accent' : 'border-control';

    const time = (which: 'fromTime' | 'toTime', label: string) =>
        form.useTime && (
            <TimeField
                label={label}
                value={form[which]}
                onChange={(value) => setRange({ [which]: value })}
                className={`${inputClass} ${border} w-[80px] shrink-0 px-2 text-center placeholder:text-ink-faint`}
            />
        );

    return (
        <div className="flex flex-col gap-[7px]">
            <div className="flex items-center gap-2">
                <span className="text-[11px] tracking-wide text-ink-dim">Time range</span>
                <span className="grow" />
                <div className="flex overflow-hidden rounded border border-control">
                    <Segment active={form.mode === 'day'} onClick={() => setRange({ mode: 'day' })}>
                        Day
                    </Segment>
                    <Segment
                        active={form.mode === 'range'}
                        // Starting the end at the start date gives a valid one-day range to widen,
                        // rather than an error before anything has been picked.
                        onClick={() => setRange({ mode: 'range', toDate: form.toDate || form.fromDate })}
                    >
                        Range
                    </Segment>
                </div>
            </div>

            {form.mode === 'day' ? (
                <>
                    <input
                        type="date"
                        aria-label="Date"
                        value={form.fromDate}
                        onChange={(e) => setRange({ fromDate: e.target.value })}
                        className={`${inputClass} ${border} w-full px-2`}
                    />
                    {form.useTime && (
                        <div className="flex items-center gap-1.5">
                            {time('fromTime', 'From time')}
                            <span className="text-xs text-ink-faint">–</span>
                            {time('toTime', 'To time')}
                        </div>
                    )}
                </>
            ) : (
                <>
                    <span className="-mb-1 text-[10.5px] text-ink-faint">From</span>
                    <div className="flex items-center gap-1.5">
                        <input
                            type="date"
                            aria-label="From date"
                            value={form.fromDate}
                            onChange={(e) => setRange({ fromDate: e.target.value })}
                            className={`${inputClass} ${border} grow px-2`}
                        />
                        {time('fromTime', 'From time')}
                    </div>
                    <span className="-mb-1 text-[10.5px] text-ink-faint">To</span>
                    <div className="flex items-center gap-1.5">
                        <input
                            type="date"
                            aria-label="To date"
                            value={form.toDate}
                            min={form.fromDate || undefined}
                            onChange={(e) => setRange({ toDate: e.target.value })}
                            className={`${inputClass} ${border} grow px-2`}
                        />
                        {time('toTime', 'To time')}
                    </div>
                </>
            )}

            <div className="flex items-center gap-2">
                <label className="flex cursor-pointer items-center gap-1.5 text-[11px] text-ink-dim">
                    <input
                        type="checkbox"
                        checked={form.useTime}
                        onChange={(e) => setRange({ useTime: e.target.checked })}
                        className="accent-accent"
                    />
                    Time of day
                </label>
                <span className="grow" />
                <button
                    type="button"
                    onClick={() => setRange({ fromDate: today(), ...(form.mode === 'range' ? { toDate: today() } : {}) })}
                    className="h-6 rounded border border-control px-2 text-[11px] text-ink-dim hover:bg-hover hover:text-ink"
                >
                    Today
                </button>
                <button
                    type="button"
                    onClick={clearRange}
                    disabled={!form.fromDate}
                    className="h-6 rounded border border-control px-2 text-[11px] text-ink-dim hover:bg-hover hover:text-ink disabled:opacity-40 disabled:hover:bg-transparent"
                >
                    Clear
                </button>
            </div>

            {resolved.kind === 'invalid' && (
                <span className="text-[10.5px] leading-snug text-warn">{resolved.reason}</span>
            )}
            {active && (
                <span className="text-[10.5px] leading-snug text-ink-faint">
                    History view: the live tail is off until the range is cleared.
                </span>
            )}
        </div>
    );
}
