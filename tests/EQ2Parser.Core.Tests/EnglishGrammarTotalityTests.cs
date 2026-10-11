using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Grammar;

namespace EQ2Parser.Core.Tests;

/// <summary>The grammar is TOTAL: no line can make it throw. An exception
/// on the line path loses that line (and, in builds up to v0.5.7, ended the
/// log's tail loop for the rest of the session),
/// and a line's text is not fully the game's to choose (a custom emote can
/// end in " damage."), so every number the patterns admit must either
/// convert or make the line a non-match. All lines here are invented shapes
/// with invented names — none is from a real log.</summary>
public class EnglishGrammarTotalityTests
{
    private static SwingEvent Swing(string message) =>
        Assert.IsType<SwingEvent>(EnglishGrammar.TryParse(message));

    [Theory]
    // The amount pattern is [\d,]+ — commas alone satisfy it.
    [InlineData(",")]
    [InlineData(",,,")]
    [InlineData(",K")]
    // 20 digits: inside decimal's range, past long's.
    [InlineData("99999999999999999999")]
    [InlineData("99,999,999,999,999,999,999")]
    // One past long.MaxValue.
    [InlineData("9223372036854775808")]
    // 32 digits: past decimal's own range.
    [InlineData("99999999999999999999999999999999")]
    // Fits before the suffix scales it, not after.
    [InlineData("9223372036854775.808K")]
    [InlineData("10,000,000,000,000M")]
    [InlineData("10,000,000,000B")]
    // Fits a decimal; the scaled product does not.
    [InlineData("70000000000000000000000000000B")]
    // \d admits every Unicode decimal digit; the number parser only ASCII.
    [InlineData("١٢٣")]
    public void Unrepresentable_Amount_Is_A_Non_Match_On_Every_Shape(string amount)
    {
        string[] lines =
        [
            $"YOUR Oak Strike hits a training dummy for {amount} crushing damage.",
            $"YOUR Oak Strike hits a training dummy for a critical of {amount} crushing damage.",
            $"YOU hit a training dummy for {amount} crushing damage.",
            $"YOU multi attack a training dummy for {amount} crushing damage.",
            $"a training dummy is hit by Oak Spray for {amount} poison damage.",
            $"Alder's Oak Strike hits a training dummy for {amount} crushing damage.",
            $"Alder hits a training dummy for {amount} crushing damage.",
            $"YOUR Oak Mending heals Birch for {amount} hit points.",
            $"Alder's Oak Mending heals Birch for a critical of {amount} hit points.",
            $"YOUR Oak Ward absorbs {amount} points of damage from being done to Birch.",
            $"Alder's Oak Ward absorbs {amount} points of damage from being done to Birch. (100 points remaining)",
            $"Alder's Oak Ward absorbs 100 points of damage from being done to Birch. ({amount} points remaining)",
            $"YOUR Oak Ward absorbs 100 points of damage from being done to Birch. ({amount} points remaining)",
            $"YOUR Oak Breeze refreshes Birch for {amount} mana points.",
            $"Alder's Oak Breeze refreshes Birch for {amount} power points.",
            $"YOUR Oak Taunt increases YOUR hate with a training dummy for {amount} threat.",
            $"Alder's Oak Taunt reduces THEIR hate with a training dummy for {amount} threat.",
        ];
        foreach (var line in lines)
        {
            // Not a zero-damage hit, not a throw: simply not a line we know.
            Assert.Null(EnglishGrammar.TryParse(line));
        }
    }

    [Theory]
    [InlineData("368", 368L)]
    [InlineData("15,032", 15_032L)]
    [InlineData("1,234.6", 1_235L)]
    [InlineData("1234.4", 1_234L)]
    [InlineData("250K", 250_000L)]
    [InlineData("140.6K", 140_600L)]
    [InlineData("1.2M", 1_200_000L)]
    [InlineData("3M", 3_000_000L)]
    [InlineData("1.5B", 1_500_000_000L)]
    [InlineData("2,000B", 2_000_000_000_000L)]
    // The largest amounts that still fit — the edge of the guard above.
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("9223372036854775.807K", long.MaxValue)]
    public void Ordinary_Amounts_Of_Every_Form_Still_Parse(string amount, long expected)
    {
        Assert.Equal(expected, Swing($"YOUR Oak Strike hits a training dummy for {amount} crushing damage.").Damage.Number);
        Assert.Equal(expected, Swing($"YOU hit a training dummy for a critical of {amount} crushing damage.").Damage.Number);
        Assert.Equal(expected, Swing($"a training dummy is hit by Oak Spray for {amount} poison damage.").Damage.Number);
        Assert.Equal(expected, Swing($"Alder's Oak Strike hits a training dummy for {amount} crushing damage.").Damage.Number);
        Assert.Equal(expected, Swing($"Alder hits a training dummy for {amount} crushing damage.").Damage.Number);
        Assert.Equal(expected, Swing($"Alder's Oak Mending heals Birch for {amount} hit points.").Damage.Number);
        Assert.Equal(expected, Swing($"Alder's Oak Breeze refreshes Birch for {amount} mana points.").Damage.Number);
        Assert.Equal(expected, Swing($"Alder's Oak Taunt increases THEIR hate with a training dummy for {amount} threat.").Damage.Number);

        var ward = Swing($"Alder's Oak Ward absorbs {amount} points of damage from being done to Birch. ({amount} points remaining)");
        Assert.Equal(expected, ward.Damage.Number);
        Assert.Equal($"remaining={expected}", ward.Extra);
    }

    [Fact]
    public void A_Refused_Amount_Leaves_The_Next_Line_Parsing()
    {
        // The processor-level statement of the same rule: the bad line is
        // skipped like chat, and the fight carries on with the real ones.
        var engine = new Engine.ParserEngine("log", "Alder");
        var processor = new Engine.LogLineProcessor(engine);
        foreach (var raw in new[]
        {
            "(1779270720)[Wed May 20 10:52:00 2026] YOU hit a training dummy for 500 crushing damage.",
            "(1779270721)[Wed May 20 10:52:01 2026] Birch hits a training dummy for , crushing damage.",
            "(1779270722)[Wed May 20 10:52:02 2026] Birch hits a training dummy for 99999999999999999999 crushing damage.",
            "(1779270723)[Wed May 20 10:52:03 2026] YOU hit a training dummy for 250 crushing damage.",
        })
        {
            Assert.True(Logs.LogLine.TryParse(raw, out var line));
            processor.Process(line);
        }
        engine.EndCombat();

        Assert.Equal(4, processor.LinesSeen);
        Assert.Equal(2, processor.LinesMatched);
        var encounter = engine.History[^1];
        Assert.Equal(750, encounter.Combatants["ALDER"].Damage);
        Assert.False(encounter.Combatants.ContainsKey("BIRCH"));
    }

    [Theory]
    // A count no lockout reaches: the unit arithmetic used to overflow
    // TimeSpan (and a merely huge one overflowed "zone-in time + lockout").
    [InlineData("This instance will expire in 99999999 days.")]
    [InlineData("This instance will expire in 2147483647 days.")]
    [InlineData("This instance will expire in 2147483647 hours.")]
    [InlineData("This instance will expire in 2147483647 days 2147483647 hours 2147483647 minutes.")]
    [InlineData("This instance will expire in 3000000 days.")]
    [InlineData("This instance will expire in 99999999999999999999 days.")]
    public void Absurd_Lockout_Is_A_Non_Match_And_Never_Throws(string message)
    {
        Assert.Null(EnglishGrammar.TryParse(message));
        Assert.Null(EnglishGrammar.TryParseInstanceLockout(message));
    }

    [Theory]
    [InlineData("This instance will expire in 7 days.", 7 * 24 * 60)]
    [InlineData("This instance will expire in 6 days 23 hours 59 minutes.", 7 * 24 * 60 - 1)]
    [InlineData("This instance will expire in 90 minutes.", 90)]
    [InlineData("This instance will expire in 28 days.", 28 * 24 * 60)]
    public void Real_Sized_Lockouts_Still_Parse(string message, int expectedMinutes)
    {
        var parsed = Assert.IsType<InstanceLockoutEvent>(EnglishGrammar.TryParse(message));
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), parsed.Remaining);
        Assert.Equal(parsed.Remaining, EnglishGrammar.TryParseInstanceLockout(message));
    }

    [Fact]
    public void A_Garbled_Ward_Remainder_Does_Not_Become_A_Zero_Remainder()
    {
        // The absorb amount is fine but the trailing count is not: the line
        // is not one the game wrote, so nothing on it is trusted.
        Assert.Null(EnglishGrammar.TryParse(
            "Alder's Oak Ward absorbs 443 points of damage from being done to Birch. (,,, points remaining)"));
        // The same line without the suffix is an ordinary ward.
        var s = Swing("Alder's Oak Ward absorbs 443 points of damage from being done to Birch.");
        Assert.Equal((SwingCategory.Healing, 443L), (s.Category, s.Damage.Number));
        Assert.Null(s.Extra);
    }
}
