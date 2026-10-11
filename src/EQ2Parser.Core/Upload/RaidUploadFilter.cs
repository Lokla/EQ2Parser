using System.Text;

namespace EQ2Parser.Core.Upload;

/// <summary>
/// The "upload only raid fights" decision — pure, so the rule is testable
/// without the queue or the settings card. A fight counts as a raid when
/// EITHER signal fires:
///
///  * its title (the strongest enemy) is on the curated raid-boss list the
///    site publishes (GET /api/zones/raid-bosses) — catches an under-manned
///    boss attempt and a boss pulled before everyone has swung; or
///  * it had at least <see cref="RaidMinPlayers"/> player allies — the
///    site's own raid scope (a group is at most six), which keeps working
///    offline / before the first sync and covers raid trash and bosses the
///    curators have not added yet.
///
/// Boss names are compared after <see cref="NormaliseBossName"/>, a mirror
/// of the site's <c>boss_key</c> (backend/server/parses/boss.py; rankings.py
/// still imports it under its old name, <c>_normalise_boss_key</c>): NFC,
/// lowercase, every apostrophe look-alike folded to <c>'</c>, odd spaces to
/// a plain space, trimmed. Keep the two in step or names silently stop
/// matching — the fold table below is the site's, row for row.
/// </summary>
public static class RaidUploadFilter
{
    /// <summary>Player allies at or above which a fight is raid-sized. EQ2
    /// groups hold six; the site's rankings use the same floor for its
    /// "raid" scope (a real raid tallies higher once pets and swap-ins are
    /// classified out, never lower).</summary>
    public const int RaidMinPlayers = 7;

    // One row per code point, in the order of the site's table
    // (_APOSTROPHE_VARIANTS in parses/boss.py) so the two read side by
    // side: fifteen apostrophe look-alikes, then six space look-alikes.
    // The keys are \u escapes on purpose — several of these characters
    // are invisible in an editor, and a literal no-break space reads as a
    // row that folds a space to a space. RaidUploadFilterTests carries
    // the same list, so a row added on one side only fails a test.
    private static readonly Dictionary<char, char> Folds = new()
    {
        ['\u0060'] = '\'', // GRAVE ACCENT
        ['\u00B4'] = '\'', // ACUTE ACCENT
        ['\u02B9'] = '\'', // MODIFIER LETTER PRIME
        ['\u02BA'] = '\'', // MODIFIER LETTER DOUBLE PRIME
        ['\u02BB'] = '\'', // MODIFIER LETTER TURNED COMMA
        ['\u02BC'] = '\'', // MODIFIER LETTER APOSTROPHE
        ['\u02BD'] = '\'', // MODIFIER LETTER REVERSED COMMA
        ['\u02BE'] = '\'', // MODIFIER LETTER RIGHT HALF RING
        ['\u02BF'] = '\'', // MODIFIER LETTER LEFT HALF RING
        ['\u02C8'] = '\'', // MODIFIER LETTER VERTICAL LINE
        ['\u2018'] = '\'', // LEFT SINGLE QUOTATION MARK
        ['\u2019'] = '\'', // RIGHT SINGLE QUOTATION MARK
        ['\u201B'] = '\'', // SINGLE HIGH-REVERSED-9 QUOTATION MARK
        ['\u2032'] = '\'', // PRIME
        ['\uFF07'] = '\'', // FULLWIDTH APOSTROPHE
        ['\u00A0'] = ' ',   // NO-BREAK SPACE
        ['\u2009'] = ' ',   // THIN SPACE
        ['\u200A'] = ' ',   // HAIR SPACE
        ['\u202F'] = ' ',   // NARROW NO-BREAK SPACE
        ['\u205F'] = ' ',   // MEDIUM MATHEMATICAL SPACE
        ['\u3000'] = ' ',   // IDEOGRAPHIC SPACE
    };

    /// <summary>The site's boss-key shape for a mob name (see the class
    /// summary). Idempotent; an already-normalised name passes through.</summary>
    public static string NormaliseBossName(string name)
    {
        var lowered = name.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var sb = new StringBuilder(lowered.Length);
        foreach (var ch in lowered)
            sb.Append(Folds.TryGetValue(ch, out var folded) ? folded : ch);
        return sb.ToString().Trim();
    }

    /// <summary>Should this finished fight upload under the raid-only rule?</summary>
    /// <param name="title">The encounter title (strongest enemy).</param>
    /// <param name="playerAllies">Allies classified as players (pets excluded).</param>
    /// <param name="raidBosses">Normalised raid-boss names from the site, or
    /// null when nothing has been synced yet (headcount alone decides).</param>
    public static bool IsRaidEncounter(string title, int playerAllies, IReadOnlySet<string>? raidBosses)
    {
        if (playerAllies >= RaidMinPlayers)
            return true;
        return raidBosses is not null && raidBosses.Contains(NormaliseBossName(title));
    }
}
