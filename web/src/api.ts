export interface MessageRow {
    id: number;
    ts: number;
    topic: string;
    correlationKey: string | null;
    qos: number;
    retained: boolean;
    size: number;
}

export interface MessageDetail extends MessageRow {
    text: string;
    encoding: 'utf-8' | 'base64';
}

export interface RecentKey {
    key: string;
    lastSeenTs: number;
    count: number;
}

export interface Broker {
    connected: boolean;
    endpoint: string;
    topics: string[];
}

export interface Status {
    broker: Broker;
    dropped: number;
    highWaterId: number;
    ringCapacity: number;
    dbPath: string;
    dbBytes: number;
    retentionDays: number;
}

export interface Settings {
    correlationPaths: string[];
}

async function get<T>(path: string, params?: Record<string, string | number | undefined>): Promise<T> {
    const query = new URLSearchParams();
    for (const [key, value] of Object.entries(params ?? {})) {
        if (value !== undefined && value !== '') query.set(key, String(value));
    }
    const suffix = query.toString();
    const response = await fetch(suffix ? `${path}?${suffix}` : path);
    if (!response.ok) {
        throw new Error(`${path} responded ${response.status}`);
    }
    return (await response.json()) as T;
}

export interface MessageQuery {
    afterId?: number;
    beforeId?: number;
    topic?: string;
    correlation?: string;
    limit?: number;
}

async function put<T>(path: string, body: unknown): Promise<T> {
    const response = await fetch(path, {
        method: 'PUT',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body),
    });
    const payload = (await response.json()) as T & { error?: string };
    if (!response.ok) {
        throw new Error(payload.error ?? `${path} responded ${response.status}`);
    }
    return payload;
}

export interface SettingsUpdate extends Settings {
    rewritten: number;
}

export const api = {
    messages: (query: MessageQuery) => get<MessageRow[]>('/api/messages', { ...query }),
    message: (id: number) => get<MessageDetail>(`/api/messages/${id}`),
    recentKeys: () => get<RecentKey[]>('/api/correlations/recent'),
    status: () => get<Status>('/api/status'),
    settings: () => get<Settings>('/api/settings'),
    saveSettings: (correlationPaths: string[]) =>
        put<SettingsUpdate>('/api/settings', { correlationPaths }),
};
