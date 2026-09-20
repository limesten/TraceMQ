import { useQuery } from '@tanstack/react-query';
import { api } from '../api';
import { groupDigits } from '../format';

export function Header() {
    const { data: status } = useQuery({
        queryKey: ['status'],
        queryFn: api.status,
        refetchInterval: 2000,
    });

    return (
        <header className="flex h-14 shrink-0 items-center gap-4 border-b border-hairline bg-panel px-5">
            <span className="text-[17px] font-semibold tracking-tight">TraceMQ</span>
            <span className="h-5 w-px bg-control" />
            {/* The real thing, from the MQTT client. A tool that always claims "connected"
                is worse than one that says nothing. */}
            <span className="flex items-center gap-2 font-mono text-xs text-ink-dim">
                <span
                    className={`size-[7px] rounded-full ${
                        status?.broker.connected ? 'bg-accent' : 'bg-warn'
                    }`}
                />
                {status ? (status.broker.connected ? 'connected' : 'disconnected') : '…'}
            </span>
            {status?.broker.endpoint && (
                <span className="font-mono text-xs text-ink-faint">{status.broker.endpoint}</span>
            )}
            <span className="grow" />
            {/*
              Hidden while the count is zero: a healthy system shows nothing here. This is the
              only place the UI admits the tool is losing data.
            */}
            {status !== undefined && status.dropped > 0 && (
                <span
                    className="flex items-center gap-2 rounded-md border border-warn-edge bg-warn-fill px-2.5 py-0.5"
                    title="Messages evicted from the ingest channel before the writer could drain them"
                >
                    <span className="size-1.5 rounded-full bg-warn" />
                    <span className="font-mono text-[11.5px] text-warn">
                        {groupDigits(status.dropped)} dropped
                    </span>
                </span>
            )}
        </header>
    );
}
