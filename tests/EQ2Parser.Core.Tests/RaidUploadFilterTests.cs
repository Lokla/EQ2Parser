using EQ2Parser.Core.Upload;

namespace EQ2Parser.Core.Tests;

/// <summary>The raid-only upload rule and its boss-name normaliser (a
/// mirror of the site's boss_key in parses/boss.py, which rankings.py still
/// aliases as _normalise_boss_key — the vectors here are the site's own
/// cases, so a drift on either side shows up as a test).</summary>
public class RaidUploadFilterTests
{
    [Theory]
    [InlineData("Trakanon", "trakanon")]
    [InlineData("  The Herald of Wuoshi  ", "the herald of wuoshi")]
    [InlineData("Vyemm’s Shade", "vyemm's shade")]          // curly apostrophe
    [InlineData("Vyemm`s Shade", "vyemm's shade")]               // grave accent
    [InlineData("Vyemmʼs Shade", "vyemm's shade")]          // modifier letter apostrophe
    [InlineData("Vyemm＇s Shade", "vyemm's shade")]          // fullwidth apostrophe
    [InlineData("Lord Vyemm", "lord vyemm")]                // no-break space
    [InlineData("Lord Vyemm", "lord vyemm")]                // thin space
    [InlineData("trakanon", "trakanon")]                         // idempotent
    public void NormaliseBossName_Matches_The_Site_Key_Shape(string input, string expected) =>
        Assert.Equal(expected, RaidUploadFilter.NormaliseBossName(input));

    // The four space look-alikes the site folded and the parser did not: a
    // boss whose name carried one INSIDE it (Trim only ever covered the
    // ends) keyed differently on the two sides and silently missed the
    // raid-boss list. Written as escapes — the characters are invisible.
    [Theory]
    [InlineData("Lord Vyemm")]   // U+200A HAIR SPACE
    [InlineData("Lord Vyemm")]   // U+202F NARROW NO-BREAK SPACE
    [InlineData("Lord Vyemm")]   // U+205F MEDIUM MATHEMATICAL SPACE
    [InlineData("Lord　Vyemm")]   // U+3000 IDEOGRAPHIC SPACE
    [InlineData("　Lord Vyemm ")] // and at the ends, where they trim away
    public void NormaliseBossName_Folds_The_Wider_Space_Set(string input) =>
        Assert.Equal("lord vyemm", RaidUploadFilter.NormaliseBossName(input));

    // The whole table, one row per code point, in the order of the site's
    // _APOSTROPHE_VARIANTS (parses/boss.py). A row added there and not here
    // is the drift this test exists to make visible: add it to both.
    [Theory]
    [InlineData('`', '\'')] // GRAVE ACCENT
    [InlineData('´', '\'')] // ACUTE ACCENT
    [InlineData('ʹ', '\'')] // MODIFIER LETTER PRIME
    [InlineData('ʺ', '\'')] // MODIFIER LETTER DOUBLE PRIME
    [InlineData('ʻ', '\'')] // MODIFIER LETTER TURNED COMMA
    [InlineData('ʼ', '\'')] // MODIFIER LETTER APOSTROPHE
    [InlineData('ʽ', '\'')] // MODIFIER LETTER REVERSED COMMA
    [InlineData('ʾ', '\'')] // MODIFIER LETTER RIGHT HALF RING
    [InlineData('ʿ', '\'')] // MODIFIER LETTER LEFT HALF RING
    [InlineData('ˈ', '\'')] // MODIFIER LETTER VERTICAL LINE
    [InlineData('‘', '\'')] // LEFT SINGLE QUOTATION MARK
    [InlineData('’', '\'')] // RIGHT SINGLE QUOTATION MARK
    [InlineData('‛', '\'')] // SINGLE HIGH-REVERSED-9 QUOTATION MARK
    [InlineData('′', '\'')] // PRIME
    [InlineData('＇', '\'')] // FULLWIDTH APOSTROPHE
    [InlineData(' ', ' ')]  // NO-BREAK SPACE
    [InlineData(' ', ' ')]  // THIN SPACE
    [InlineData(' ', ' ')]  // HAIR SPACE
    [InlineData(' ', ' ')]  // NARROW NO-BREAK SPACE
    [InlineData(' ', ' ')]  // MEDIUM MATHEMATICAL SPACE
    [InlineData('　', ' ')]  // IDEOGRAPHIC SPACE
    public void NormaliseBossName_Folds_Every_Code_Point_The_Site_Folds(char variant, char folded) =>
        Assert.Equal($"vy{folded}emm", RaidUploadFilter.NormaliseBossName($"Vy{variant}emm"));

    [Theory]
    // Deliberately NOT folded on either side — a plain apostrophe and a
    // plain space pass through, and so do look-alikes outside the table
    // (a double quote, an en space): the two sides must agree on what is
    // left alone as much as on what is folded.
    [InlineData("Vy'emm", "vy'emm")]
    [InlineData("Vy emm", "vy emm")]
    [InlineData("Vy\"emm", "vy\"emm")]
    [InlineData("Vy emm", "vy emm")] // U+2002 EN SPACE
    public void NormaliseBossName_Leaves_Everything_Else_Alone(string input, string expected) =>
        Assert.Equal(expected, RaidUploadFilter.NormaliseBossName(input));

    [Fact]
    public void NormaliseBossName_Applies_NFC()
    {
        // e + combining acute (NFD) folds to the precomposed é (NFC).
        Assert.Equal("zé", RaidUploadFilter.NormaliseBossName("Zé"));
    }

    [Fact]
    public void Raid_Sized_Fights_Upload_Without_A_Boss_List()
    {
        Assert.True(RaidUploadFilter.IsRaidEncounter("a krait warrior", RaidUploadFilter.RaidMinPlayers, null));
        Assert.True(RaidUploadFilter.IsRaidEncounter("a krait warrior", 24, null));
        Assert.False(RaidUploadFilter.IsRaidEncounter("a krait warrior", RaidMinPlayersMinusOne, null));
        Assert.False(RaidUploadFilter.IsRaidEncounter("Trakanon", 0, null));
    }

    private const int RaidMinPlayersMinusOne = RaidUploadFilter.RaidMinPlayers - 1;

    [Fact]
    public void Curated_Boss_Uploads_At_Any_Headcount()
    {
        var bosses = new HashSet<string>(StringComparer.Ordinal) { "trakanon", "vyemm's shade" };
        Assert.True(RaidUploadFilter.IsRaidEncounter("Trakanon", 1, bosses));
        Assert.True(RaidUploadFilter.IsRaidEncounter("Vyemm’s Shade", 3, bosses)); // curly on the log side
        Assert.False(RaidUploadFilter.IsRaidEncounter("Lord Bob", 6, bosses));
        Assert.False(RaidUploadFilter.IsRaidEncounter("a krait warrior", 6, bosses));
    }

    [Fact]
    public void Curated_Boss_Matches_Through_An_Odd_Space_In_The_Log_Title()
    {
        // The list arrives already normalised by the site; the log side
        // carries whatever the client wrote between the words.
        var bosses = new HashSet<string>(StringComparer.Ordinal) { "lord vyemm" };
        Assert.True(RaidUploadFilter.IsRaidEncounter("Lord Vyemm", 2, bosses)); // narrow no-break space
        Assert.True(RaidUploadFilter.IsRaidEncounter("Lord　Vyemm", 2, bosses)); // ideographic space
        Assert.True(RaidUploadFilter.IsRaidEncounter("Lord Vyemm", 2, bosses));
        Assert.False(RaidUploadFilter.IsRaidEncounter("LordVyemm", 2, bosses));
    }

    [Fact]
    public void Group_Size_Is_Six()
    {
        // The site's raid scope starts at 7 — a full group never uploads on headcount.
        Assert.Equal(7, RaidUploadFilter.RaidMinPlayers);
    }
}

public class RaidBossPackTests
{
    [Fact]
    public void Parses_The_Wire_Shape_And_Normalises_Names()
    {
        var pack = RaidBossPack.Parse("""{"version":"abc123def456","bosses":["Trakanon"," vyemm’s shade ",""]}""");
        Assert.NotNull(pack);
        Assert.Equal("abc123def456", pack!.Version);
        var set = pack.ToSet();
        Assert.Equal(2, set.Count);
        Assert.Contains("trakanon", set);
        Assert.Contains("vyemm's shade", set);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"version":"x","bosses":null}""")]
    [InlineData("[]")]
    public void Malformed_Or_Listless_Bodies_Parse_To_Null(string body) =>
        Assert.Null(RaidBossPack.Parse(body));
}
