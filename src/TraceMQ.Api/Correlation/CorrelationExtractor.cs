using System.Globalization;
using System.Text.Json;

namespace TraceMQ.Api.Correlation;

/// <summary>
/// Pulls the correlation key out of a payload, using the first of the configured paths that
/// resolves to a scalar. Runs on the MQTT receive loop for every message, so it is a single
/// forward pass that bails at the first mismatch, and it never throws: a payload that is not
/// JSON, or does not carry the path, simply has no key.
/// </summary>
public sealed class CorrelationExtractor
{
    /// <summary>
    /// A safety valve, not an optimisation. A forward pass over a normal payload is
    /// microseconds; this only stops one pathological message from stalling the receive loop.
    /// </summary>
    public const int MaxPayloadBytes = 4 * 1024 * 1024;

    public const int MaxPaths = 5;

    private readonly string[][] _paths;

    public CorrelationExtractor(IEnumerable<string> paths)
    {
        _paths = paths
            .Select(Normalize)
            .Where(segments => segments.Length > 0)
            .Take(MaxPaths)
            .ToArray();
    }

    public IReadOnlyList<string> Paths => _paths.Select(p => string.Join('.', p)).ToArray();

    /// <summary>Dotted, with or without a leading <c>$.</c>, as the UI writes it.</summary>
    public static string[] Normalize(string path)
    {
        var trimmed = (path ?? string.Empty).Trim();
        if (trimmed.StartsWith("$.", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }
        return trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public string? Extract(ReadOnlySpan<byte> payload)
    {
        if (_paths.Length == 0 || payload.IsEmpty || payload.Length > MaxPayloadBytes)
        {
            return null;
        }

        foreach (var segments in _paths)
        {
            string? value;
            try
            {
                var reader = new Utf8JsonReader(payload, isFinalBlock: true, state: default);
                value = Find(ref reader, segments);
            }
            catch (JsonException)
            {
                // Malformed JSON, or not JSON at all. No key, and no reason to try the other
                // paths against the same bytes.
                return null;
            }

            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static string? Find(ref Utf8JsonReader reader, string[] segments)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            // A bare scalar or array at the root carries no named path.
            return null;
        }

        var matched = 0;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    // Properties of the subtree we are currently inside sit one level deeper
                    // than the number of segments matched so far.
                    if (reader.CurrentDepth == matched + 1 && reader.ValueTextEquals(segments[matched]))
                    {
                        matched++;
                        if (!reader.Read())
                        {
                            return null;
                        }

                        if (matched == segments.Length)
                        {
                            return ReadScalar(ref reader);
                        }

                        if (reader.TokenType != JsonTokenType.StartObject)
                        {
                            // The path continues but the value does not. Nothing to descend into.
                            return null;
                        }
                    }
                    else
                    {
                        // Skip the whole value rather than walking it.
                        reader.Read();
                        reader.Skip();
                    }
                    break;

                case JsonTokenType.EndObject:
                    if (reader.CurrentDepth < matched)
                    {
                        // Left the subtree that held the matched prefix without finding the rest.
                        return null;
                    }
                    break;
            }
        }

        return null;
    }

    private static string? ReadScalar(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => reader.TryGetInt64(out var whole)
            ? whole.ToString(CultureInfo.InvariantCulture)
            : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        // Null, an object or an array: the path exists but carries no key.
        _ => null,
    };
}
