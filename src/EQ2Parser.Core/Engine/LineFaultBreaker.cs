namespace EQ2Parser.Core.Engine;

/// <summary>What the pump does with a line whose processing threw.
/// <paramref name="Stop"/>: the breaker tripped — end the tail loop.
/// <paramref name="Journal"/>: this failure is due a breadcrumb (the first
/// one, one per <see cref="LineFaultBreaker.JournalInterval"/> after that,
/// and always the one that trips). <paramref name="Skipped"/> is the running
/// total, so a throttled breadcrumb still accounts for every line dropped
/// since the previous one.</summary>
public readonly record struct LineFaultOutcome(bool Stop, bool Journal, long Skipped, int Consecutive);

/// <summary>
/// The policy half of per-line fault isolation: a log pump that catches an
/// exception from ONE line asks this whether to skip the line and carry on
/// or to stop the source. Pure bookkeeping (no IO, no clock of its own) so
/// the rule is testable without a file or a thread; the try/catch and the
/// evidence writing live with the pump.
///
/// Why a breaker at all: one poison line must never end a raid night's
/// parse, but a fault that hits EVERY line (a broken type initializer, a
/// dead dependency) is structural — carrying on only burns CPU on throws
/// while the Sources page keeps claiming the log is being read.
///
/// Why the threshold counts CONSECUTIVE failures, and why it is 200: a
/// single bad line shape arrives in blocks — one raid-wide AE writes the
/// same shape once per target, back to back (24 players, plus pets, plus
/// swarm pets: 50-ish lines, and a second AE can land in the same batch).
/// 200 clears several such blocks with room to spare, while a structural
/// fault reaches it within a second or two of raid combat and at once on an
/// import. The asymmetry is deliberate: a late trip costs a few hundred
/// cheap throws on lines that were failing anyway, a false trip silently
/// loses the rest of the raid.
///
/// Thread model: ONE writer (the source's pump thread) calls the Record
/// methods; the UI reads the counters for the Sources page, hence the
/// interlocked/volatile reads.
/// </summary>
public sealed class LineFaultBreaker(int tripAfter = LineFaultBreaker.DefaultTripAfter)
{
    /// <summary>Back-to-back failed lines that stop a source.</summary>
    public const int DefaultTripAfter = 200;

    /// <summary>Minimum gap between journal breadcrumbs for skipped lines.
    /// Failures that alternate with good lines never trip the breaker, so
    /// an import could otherwise append a journal line per bad line —
    /// millions of them.</summary>
    public static readonly TimeSpan JournalInterval = TimeSpan.FromSeconds(10);

    private readonly int _tripAfter = tripAfter > 0
        ? tripAfter
        : throw new ArgumentOutOfRangeException(nameof(tripAfter), "The breaker needs a positive threshold.");

    private long _skipped;
    private int _consecutive;
    private volatile Exception? _lastError;
    private volatile bool _tripped;
    private DateTimeOffset? _lastJournal;

    /// <summary>Lines dropped because processing them threw.</summary>
    public long SkippedLines => Interlocked.Read(ref _skipped);

    /// <summary>Failed lines since the last line that processed cleanly.</summary>
    public int ConsecutiveFailures => Volatile.Read(ref _consecutive);

    /// <summary>The most recent line failure, or null when none has
    /// happened. Kept after a recovery — "3 lines skipped" needs its why.</summary>
    public Exception? LastError => _lastError;

    /// <summary>The threshold was reached; the source must stop.</summary>
    public bool Tripped => _tripped;

    /// <summary>A line went all the way through — the failure run is over.
    /// Lines the pump never attempted (not EQ2-shaped) must NOT be reported
    /// here: they prove nothing about whether processing works.</summary>
    public void RecordSuccess()
    {
        // The read keeps the common case (no failures, ever) write-free on
        // the per-line hot path.
        if (Volatile.Read(ref _consecutive) != 0)
            Volatile.Write(ref _consecutive, 0);
    }

    /// <summary>A line's processing threw. <paramref name="now"/> only
    /// drives the breadcrumb throttle.</summary>
    public LineFaultOutcome RecordFailure(Exception error, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(error);
        _lastError = error;
        var skipped = Interlocked.Increment(ref _skipped);
        var consecutive = Interlocked.Increment(ref _consecutive);
        var stop = consecutive >= _tripAfter;
        if (stop)
            _tripped = true;
        var journal = stop || _lastJournal is not { } last || now - last >= JournalInterval;
        if (journal)
            _lastJournal = now;
        return new LineFaultOutcome(stop, journal, skipped, consecutive);
    }
}
