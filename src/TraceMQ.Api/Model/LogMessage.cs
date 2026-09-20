namespace TraceMQ.Api.Model;

/// <summary>
/// One captured message. <see cref="Id"/> is assigned by ingest, not by SQLite, so the ring
/// buffer and the database number the same message identically and the UI can page from one
/// into the other (ARCHITECTURE.md section 4, rule 5).
/// </summary>
public sealed record LogMessage(
    long Id,
    long TimestampMs,
    string Topic,
    byte[] Payload,
    byte Qos,
    bool Retained,
    string? CorrelationKey
);
