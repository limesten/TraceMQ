import { create } from 'zustand';
import { EMPTY_RANGE, type RangeForm } from './timeRange';

interface ViewState {
    topicFilter: string;
    correlationSearch: string;
    range: RangeForm;
    paused: boolean;
    autoScroll: boolean;
    settingsOpen: boolean;
    selectedId: number | null;

    setTopicFilter: (value: string) => void;
    setCorrelationSearch: (value: string) => void;
    setRange: (patch: Partial<RangeForm>) => void;
    clearRange: () => void;
    togglePaused: () => void;
    toggleAutoScroll: () => void;
    toggleSettings: () => void;
    select: (id: number) => void;
}

/** Filters and selection. Nothing else: server data belongs to TanStack Query. */
export const useView = create<ViewState>((set) => ({
    topicFilter: 'codeit/#',
    correlationSearch: '',
    range: EMPTY_RANGE,
    paused: false,
    autoScroll: true,
    settingsOpen: false,
    selectedId: null,

    setTopicFilter: (topicFilter) => set({ topicFilter }),
    // Searching a sequence is a look at history, so it drops the live view's selection
    // rather than leaving a row highlighted that the new result set does not contain.
    setCorrelationSearch: (correlationSearch) => set({ correlationSearch, selectedId: null }),
    // A time range is the same kind of look at history.
    setRange: (patch) => set((s) => ({ range: { ...s.range, ...patch }, selectedId: null })),
    clearRange: () => set({ range: EMPTY_RANGE, selectedId: null }),
    togglePaused: () => set((s) => ({ paused: !s.paused })),
    toggleAutoScroll: () => set((s) => ({ autoScroll: !s.autoScroll })),
    toggleSettings: () => set((s) => ({ settingsOpen: !s.settingsOpen })),
    select: (selectedId) => set({ selectedId }),
}));
