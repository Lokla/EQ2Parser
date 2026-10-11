using System.Text.RegularExpressions;

namespace EQ2Parser.Core.Raid;

/// <summary>One row of /who output. Anonymous players expose only the name
/// (and possibly the AFK suffix) — every other field is null for them.</summary>
public sealed record WhoRow(string Name, int? Level, string? Class, string? Race, string? Guild, string? Zone, bool Afk);

/// <summary>A complete /who result block (header → rows → "N players found").
/// <paramref name="FromWhoraid"/>: the block came from the self-identifying
/// "/whoraid search results…" header — definitively the raid roster. Plain
/// "/who search results…" headers are ambiguous (guild / zone / server-wide
/// "/who all") and are only trusted as guild data by pair position.</summary>
public sealed record WhoResult(string? ZoneFilter, IReadOnlyList<WhoRow> Rows, DateTimeOffset CompletedAt, bool FromWhoraid);

/// <summary>
/// Stateful assembler for /who output blocks (verbatim live shapes, mined
/// from real logs 2026-09):
///
///   /who search results:                          (or "…results for &lt;Zone&gt;:")
///   /whoraid search results for Qeynos Province District:
///   ------------------------------------------
///   [70 Conjuror] Tsuna (Freeblood) &lt;Exordium&gt; Zone: Throne of New Tunaria
///   [70 Templar] Menludiir (Gnome)                (whoraid rows: no guild/zone)
///   [Anonymous] Betabonk
///   [Anonymous] Ashtar (AFK)
///   24 players found                              (or "1 player found")
///
/// The refresh macro runs "whoraid" (echoed as "/whoraid search results for
/// &lt;your zone&gt;:") then "who all guild" — both header shapes are one block
/// start here; positional raid/guild classification is the tracker's job.
///
/// Feed every candidate line with its log timestamp; a completed block is
/// returned from <see cref="Feed"/> when the footer lands. Unrelated lines
/// while a block is open are ignored (chat can interleave); a block is
/// abandoned when a new header arrives, on "There are no players…", after
/// <see cref="MaxRows"/> rows, or when a line arrives more than
/// <see cref="BlockTimeout"/> after the header (who output bursts within a
/// second — anything later is not part of the block).
/// </summary>
public sealed partial class WhoParser
{
    public const int MaxRows = 200;
    public static readonly TimeSpan BlockTimeout = TimeSpan.FromSeconds(5);

    [GeneratedRegex(@"^/who(?<raidcmd>raid)? search results(?: for (?<zone>.+?))?:$")]
    private static partial Regex HeaderRegex();

    // (AFK) appears in two live positions: after the zone (zone who) and
    // between the guild tag and "Zone:" (guild who — see the 2026-09-02
    // macro capture) — both are accepted.
    [GeneratedRegex(
        @"^\[(?<level>\d+) (?<cls>[A-Za-z]+)\] (?<name>[A-Za-z]+) \((?<race>[A-Za-z ']+)\)(?: <(?<guild>[^>]+)>)?(?<afk1> \(AFK\))?(?: Zone: (?<zone>.+?))?(?<afk2> \(AFK\))?$")]
    private static partial Regex DetailRowRegex();

    [GeneratedRegex(@"^\[Anonymous\] (?<name>[A-Za-z]+)(?<afk> \(AFK\))?$")]
    private static partial Regex AnonRowRegex();

    [GeneratedRegex(@"^\d+ players? found$")]
    private static partial Regex FooterRegex();

    private string? _zoneFilter;
    private List<WhoRow>? _rows;
    private DateTimeOffset _headerAt;
    private bool _fromWhoraid;

    /// <summary>Cheap pre-check callers can use before paying for Feed.</summary>
    public static bool LooksRelevant(string message) =>
        message.StartsWith("/who search results", StringComparison.Ordinal)
        || message.StartsWith("/whoraid search results", StringComparison.Ordinal)
        || message.StartsWith('[')
        || message.EndsWith("player found", StringComparison.Ordinal)
        || message.EndsWith("players found", StringComparison.Ordinal);

    /// <summary>Feed one log line. Returns a completed block when this line
    /// was its footer, else null.</summary>
    public WhoResult? Feed(string message, DateTimeOffset time)
    {
        var header = HeaderRegex().Match(message);
        if (header.Success)
        {
            _zoneFilter = header.Groups["zone"].Success ? header.Groups["zone"].Value : null;
            _fromWhoraid = header.Groups["raidcmd"].Success;
            _rows = [];
            _headerAt = time;
            return null;
        }
        if (_rows is null)
            return null;
        if (time - _headerAt > BlockTimeout)
        {
            _rows = null; // stale block — this line belongs to something else
            return null;
        }
        if (FooterRegex().IsMatch(message))
        {
            var result = new WhoResult(_zoneFilter, _rows, time, _fromWhoraid);
            _rows = null;
            return result;
        }
        var detail = DetailRowRegex().Match(message);
        // TryParse, not Parse: \d+ admits a level too long for an int (and
        // non-ASCII digits), and a throw here is a throw on the log-line
        // path. A row whose level cannot be read is not a /who row.
        if (detail.Success
            && int.TryParse(detail.Groups["level"].Value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var level))
        {
            _rows.Add(new WhoRow(
                detail.Groups["name"].Value,
                level,
                detail.Groups["cls"].Value,
                detail.Groups["race"].Value,
                detail.Groups["guild"].Success ? detail.Groups["guild"].Value : null,
                detail.Groups["zone"].Success ? detail.Groups["zone"].Value.Trim() : null,
                detail.Groups["afk1"].Success || detail.Groups["afk2"].Success));
        }
        else
        {
            var anon = AnonRowRegex().Match(message);
            if (anon.Success)
                _rows.Add(new WhoRow(anon.Groups["name"].Value, null, null, null, null, null, anon.Groups["afk"].Success));
            // else: unrelated interleaved line — ignore, block stays open
        }
        if (_rows.Count > MaxRows)
            _rows = null; // runaway — abandon
        return null;
    }
}
