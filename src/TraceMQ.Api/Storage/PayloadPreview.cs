using System.Text;

namespace TraceMQ.Api.Storage;

/// <summary>
/// The one-line payload shown in the table.
///
/// Built from a fixed head of the payload, never the whole thing: the list endpoint stays
/// small by design, and a preview of a bounded length keeps a 500-row page in tens of
/// kilobytes rather than megabytes.
/// </summary>
public static class PayloadPreview
{
    /// <summary>Bytes read from the front of the payload.</summary>
    public const int HeadBytes = 256;

    /// <summary>Characters kept after collapsing whitespace.</summary>
    public const int MaxChars = 160;

    /// <summary>
    /// Null for a payload that is not text. The row already carries its size, so the UI can
    /// say "binary" without the preview pretending to be characters.
    /// </summary>
    public static string? From(byte[]? head)
    {
        if (head is null || head.Length == 0) return null;

        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);

        // The head is a byte cut, so it can land inside a multi-byte character. Inspecting the
        // trailing bytes to guess where the character starts cannot tell a COMPLETE sequence
        // from a cut one — both end in a continuation byte — so this asks the decoder instead,
        // dropping at most the three bytes a cut character can leave behind. A payload that is
        // not text fails all four attempts.
        for (var drop = 0; drop <= 3 && head.Length - drop > 0; drop++)
        {
            try
            {
                return Flatten(strict.GetString(head, 0, head.Length - drop));
            }
            catch (DecoderFallbackException)
            {
                // Try again a byte shorter.
            }
        }

        return null;
    }

    /// <summary>
    /// One line. Newlines and runs of whitespace become single spaces, so a pretty-printed
    /// payload reads as compactly as a minified one, and control characters are dropped
    /// rather than smearing the row.
    /// </summary>
    private static string? Flatten(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, MaxChars));
        var pendingSpace = false;

        foreach (var ch in text)
        {
            if (builder.Length >= MaxChars) break;

            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (char.IsControl(ch)) continue;

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
                if (builder.Length >= MaxChars) break;
            }
            builder.Append(ch);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}
