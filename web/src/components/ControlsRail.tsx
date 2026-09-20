import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { api } from '../api';
import { formatDelta } from '../format';
import { useView } from '../store';

function Toggle({ on, onToggle, label }: { on: boolean; onToggle: () => void; label: string }) {
    return (
        <button
            type="button"
            role="switch"
            aria-checked={on}
            aria-label={label}
            onClick={onToggle}
            className={`flex h-[26px] w-[46px] items-center rounded-full border px-[3px] transition-colors ${
                on ? 'justify-end border-accent bg-accent-fill' : 'justify-start border-control bg-hover'
            }`}
        >
            <span className={`size-[18px] rounded-full ${on ? 'bg-accent' : 'bg-topic-head'}`} />
        </button>
    );
}

function Cog() {
    return (
        <svg
            width="13"
            height="13"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
        >
            <circle cx="12" cy="12" r="3" />
            <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.6a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
        </svg>
    );
}

/**
 * The correlation path editor. It exists as its own component so that opening the panel
 * mounts it fresh: the draft initialises from the server's paths in a useState initialiser,
 * and closing unmounts it, which is what Cancel means. Syncing props into state from an
 * effect instead would start a second render every time the panel opened, and leave the
 * draft alive after it closed.
 */
export function PathEditor({ initial, onDone }: { initial: string[]; onDone: () => void }) {
    const queryClient = useQueryClient();
    const [draft, setDraft] = useState(() => initial.join('\n'));

    const save = useMutation({
        mutationFn: (paths: string[]) => api.saveSettings(paths),
        onSuccess: async () => {
            // The key changed on every stored message, so nothing cached still holds.
            await queryClient.invalidateQueries();
            onDone();
        },
    });

    return (
        <div className="flex flex-col gap-2 rounded-lg border border-control bg-hover p-3">
            <label htmlFor="corrPaths" className="text-[11px] font-medium text-ink">
                Correlation paths
            </label>
            <span className="text-[10.5px] leading-snug text-ink-dim">
                One per line. The first that resolves wins.
            </span>
            <textarea
                id="corrPaths"
                rows={3}
                spellCheck={false}
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                disabled={save.isPending}
                className="resize-none rounded border border-control bg-ground p-2 font-mono text-[11px] leading-relaxed text-ink disabled:opacity-50"
            />
            <span className="text-[10.5px] leading-snug text-ink-faint">
                Saving rewrites the key on every stored message.
            </span>
            {save.isError && (
                <span className="text-[10.5px] leading-snug text-warn">
                    {(save.error as Error).message}
                </span>
            )}
            <div className="flex gap-2">
                <button
                    type="button"
                    className="h-7 grow rounded border border-control text-xs text-ink-dim hover:bg-hover disabled:opacity-50"
                    onClick={onDone}
                    disabled={save.isPending}
                >
                    Cancel
                </button>
                <button
                    type="button"
                    className="h-7 grow rounded border border-accent bg-accent-fill text-xs font-medium text-accent disabled:opacity-50"
                    onClick={() => save.mutate(draft.split('\n'))}
                    disabled={save.isPending}
                >
                    {save.isPending ? 'Rewriting…' : 'Save'}
                </button>
            </div>
        </div>
    );
}

/** A clock that advances on its own, for relative timestamps. */
function useTicker(everyMs: number): number {
    const [now, setNow] = useState(() => Date.now());

    useEffect(() => {
        const timer = setInterval(() => setNow(Date.now()), everyMs);
        return () => clearInterval(timer);
    }, [everyMs]);

    return now;
}

const fieldClass =
    'h-8 w-full rounded-md border bg-ground px-2.5 font-mono text-xs text-ink placeholder:text-ink-faint';

export function ControlsRail() {
    const view = useView();

    const { data: recent = [] } = useQuery({
        queryKey: ['recentKeys'],
        queryFn: api.recentKeys,
        refetchInterval: view.paused ? false : 5000,
    });

    const { data: settings } = useQuery({ queryKey: ['settings'], queryFn: api.settings });

    // Date.now() during render never updates, so "2 min ago" would stay "2 min ago" until
    // the query happened to refetch. Tick it deliberately instead.
    const now = useTicker(30_000);

    return (
        <aside className="flex w-[264px] shrink-0 flex-col gap-[18px] overflow-y-auto border-r border-hairline bg-panel px-4 py-[18px] scroll-thin">
            <div className="flex items-start gap-3.5">
                <div className="flex flex-col gap-[7px]">
                    <span className="text-[11px] tracking-wide text-ink-dim">Auto scroll</span>
                    <Toggle on={view.autoScroll} onToggle={view.toggleAutoScroll} label="Auto scroll" />
                </div>
                <div className="flex flex-col gap-[7px]">
                    <span className="text-[11px] tracking-wide text-ink-dim">Stream</span>
                    <button
                        type="button"
                        onClick={view.togglePaused}
                        className={`h-[26px] rounded-md border px-3.5 text-xs font-medium ${
                            view.paused
                                ? 'border-warn bg-warn-fill text-warn'
                                : 'border-control bg-hover text-topic hover:bg-selected'
                        }`}
                    >
                        {view.paused ? 'Resume' : 'Pause'}
                    </button>
                </div>
            </div>

            <div className="flex flex-col gap-[7px]">
                <label htmlFor="topicFilter" className="text-[11px] tracking-wide text-ink-dim">
                    Topic filter
                </label>
                <input
                    id="topicFilter"
                    type="text"
                    spellCheck={false}
                    value={view.topicFilter}
                    onChange={(e) => view.setTopicFilter(e.target.value)}
                    className={`${fieldClass} border-control`}
                />
            </div>

            <div className="flex flex-col gap-[7px]">
                <div className="flex items-center gap-2">
                    <label htmlFor="corrSearch" className="text-[11px] tracking-wide text-ink-dim">
                        Correlation search
                    </label>
                    <span className="grow" />
                    <button
                        type="button"
                        aria-label="Correlation path settings"
                        aria-expanded={view.settingsOpen}
                        onClick={view.toggleSettings}
                        className={`flex size-6 items-center justify-center rounded border ${
                            view.settingsOpen
                                ? 'border-accent bg-accent-fill text-accent'
                                : 'border-control text-ink-dim hover:bg-hover hover:text-ink'
                        }`}
                    >
                        <Cog />
                    </button>
                </div>
                <input
                    id="corrSearch"
                    type="text"
                    spellCheck={false}
                    placeholder="paste a correlation key"
                    value={view.correlationSearch}
                    onChange={(e) => view.setCorrelationSearch(e.target.value)}
                    className={`${fieldClass} text-[11px] ${
                        view.correlationSearch ? 'border-accent' : 'border-control'
                    }`}
                />
            </div>

            {/* Mounted only while open, and only once the server's paths are known, so it
                initialises from them without an effect syncing props into state. */}
            {view.settingsOpen && settings && (
                <PathEditor initial={settings.correlationPaths} onDone={view.toggleSettings} />
            )}

            <span className="grow" />

            <div className="flex flex-col gap-2">
                <span className="text-[11px] tracking-wide text-ink-dim">Recent correlation keys</span>
                {recent.length === 0 && (
                    <span className="text-[10.5px] text-ink-faint">None seen yet.</span>
                )}
                {recent.map((entry) => {
                    const active = view.correlationSearch === entry.key;
                    return (
                        <button
                            key={entry.key}
                            type="button"
                            onClick={() => view.setCorrelationSearch(active ? '' : entry.key)}
                            className={`flex flex-col gap-0.5 rounded border px-2 py-[7px] text-left ${
                                active
                                    ? 'border-accent bg-selected'
                                    : 'border-hairline hover:border-control hover:bg-hover'
                            }`}
                        >
                            <span
                                className={`font-mono text-[10.5px] tracking-tight ${
                                    active ? 'text-accent' : 'text-topic'
                                }`}
                            >
                                {entry.key}
                            </span>
                            <span className="text-[10px] text-ink-faint">
                                {formatDelta(now - entry.lastSeenTs)} ago · {entry.count} messages
                            </span>
                        </button>
                    );
                })}
            </div>
        </aside>
    );
}
