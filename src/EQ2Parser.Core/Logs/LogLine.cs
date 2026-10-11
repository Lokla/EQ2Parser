namespace EQ2Parser.Core.Logs;

/// <summary>
/// One parsed EQ2 log line. The on-disk shape is:
/// <c>(1753738000)[Mon Jul 28 22:26:40 2026] You hit a training dummy for 100 points of crushing damage.</c>
/// — a unix epoch in parentheses, the client's local-time string in brackets,
/// then the message. The epoch is authoritative for all timing; the bracketed
/// string is locale/DST-ambiguous and skipped entirely (materialising it was
/// one dead string allocation per parsed line).
/// </summary>
public readonly record struct LogLine(long Epoch, string Message)
{
    /// <summary>Wall-clock arrival stamp from the tail reader (the app's
    /// second clock — sub-second, live mode only). Null on imports. Never
    /// feeds ACT/site-compatible stat math; see TailedLine.</summary>
    public DateTimeOffset? ObservedAt { get; init; }

    /// <summary>
    /// Parse a raw log line. Returns false for anything that doesn't match the
    /// EQ2 shape (corrupt tail reads, BOMs, other games' logs) — callers skip
    /// those rather than throw, because a live tail WILL see partial lines.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> raw, out LogLine line)
    {
        line = default;
        if (raw.Length < 8 || raw[0] != '(')
            return false;

        var closeParen = raw.IndexOf(')');
        if (closeParen < 2 || !long.TryParse(raw[1..closeParen], out var epoch))
            return false;
        // A number that parses is not yet a time: Timestamp converts with
        // DateTimeOffset.FromUnixTimeSeconds, which THROWS outside the
        // years 1..9999. A corrupt or forged prefix such as
        // "(999999999999999999)[x] ..." is therefore "not an EQ2 line",
        // the same as any other malformed prefix, rather than a line that
        // blows up the first time something asks it what time it is.
        if (epoch < MinEpoch || epoch > MaxEpoch)
            return false;

        var rest = raw[(closeParen + 1)..];
        if (rest.Length < 3 || rest[0] != '[')
            return false;

        var closeBracket = rest.IndexOf(']');
        if (closeBracket < 1)
            return false;

        var message = rest[(closeBracket + 1)..].TrimStart(' ');
        line = new LogLine(epoch, message.ToString());
        return true;
    }

    /// <summary>The epoch range <see cref="Timestamp"/> can represent
    /// (0001-01-01 to 9999-12-31 UTC). TryParse refuses anything outside it.</summary>
    private static readonly long MinEpoch = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaxEpoch = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeSeconds(Epoch);
}
