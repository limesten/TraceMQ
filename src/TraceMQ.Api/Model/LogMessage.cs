namespace TraceMQ.Api.Model;

public sealed record LogMessage(
    long TimestampMs,
    string Topic,
    byte[] Payload,
    byte Qos,
    bool Retained,
    string? CorrelationKey = null
);
