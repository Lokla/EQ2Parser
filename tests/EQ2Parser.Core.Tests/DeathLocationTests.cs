using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Grammar;
using EQ2Parser.Core.Logs;

namespace EQ2Parser.Core.Tests;

/// <summary>Deaths that some clients log with a location after the victim's
/// name (" in &lt;zone&gt; (&lt;x&gt;, &lt;y&gt;, &lt;z&gt;)"). The victim strings are verbatim
/// from uploaded fights, with player names swapped. The sentence around them
/// is NOT from a real log: no sample log has one, only the names the old
/// build captured, so every death shape is covered.</summary>
public class DeathLocationTests
{
    private const string Located = "Doomcoil in The Protector's Realm (29.08, -10.09, 298.89)";

    [Theory]
    [InlineData("Alas, " + Located + " has died from pain and suffering.", "Unknown")]
    [InlineData(Located + " dies.", "Unknown")]
    [InlineData(Located + " has been banished!", "Unknown")]
    [InlineData("Sofja has killed " + Located + ".", "Sofja")]
    [InlineData(Located + " has been slain by Sofja!", "Sofja")]
    [InlineData("You have killed " + Located + ".", "YOU")]
    public void The_Location_Is_Not_Part_Of_The_Victims_Name(string message, string killer)
    {
        var death = Assert.IsType<DeathEvent>(EnglishGrammar.TryParse(message));
        Assert.Equal((killer, "Doomcoil"), (death.Killer, death.Victim));
    }

    [Theory]
    // A pet, a zone with an instance number, a zone with a colon, whole-number coordinates.
    [InlineData("Sofja's doppelganger in The Protector's Realm (27.38, -10.09, 292.99)", "Sofja's doppelganger")]
    [InlineData("a sebilite golem in Sebilis 4 (-112.5, 3.25, 100.01)", "a sebilite golem")]
    [InlineData("Drusella Sathir in Charasis: Maiden's Chamber (0.00, -4.10, 12.00)", "Drusella Sathir")]
    [InlineData("Sofja in Kunzar Jungle 2 (12, -3, 400)", "Sofja")]
    // Not a location: the name is kept whole.
    [InlineData("a wolf in sheep's clothing", "a wolf in sheep's clothing")]
    [InlineData("a ghoul in The Crypt (elder)", "a ghoul in The Crypt (elder)")]
    [InlineData("Lord Bob (29.08, -10.09, 298.89)", "Lord Bob (29.08, -10.09, 298.89)")]
    [InlineData("a keeper in Sebilis (29.08, -10.09)", "a keeper in Sebilis (29.08, -10.09)")]
    public void Only_A_Zone_And_Three_Coordinates_Are_Dropped(string victim, string expected)
    {
        var death = Assert.IsType<DeathEvent>(EnglishGrammar.TryParse($"Alas, {victim} has died from pain and suffering."));
        Assert.Equal(expected, death.Victim);
    }

    private static string Raw(long epoch, string message) =>
        $"({epoch})[Sat Aug 1 20:44:48 2026] {message}";

    // The reported fight: the boss took all its damage as "Doomcoil" and died
    // as "Doomcoil in The Protector's Realm (...)", so the boss had no death
    // and a clean kill uploaded as a partial.
    [Fact]
    public void A_Boss_That_Dies_With_A_Location_Is_A_Win()
    {
        var engine = new ParserEngine("log", "Sofja");
        var processor = new LogLineProcessor(engine);
        string[] lines =
        [
            Raw(100, "YOU hit Doomcoil for 5,000 piercing damage."),
            Raw(101, "Doomcoil hits YOU for 2,000 crushing damage."),
            Raw(102, "YOU hit Doomcoil for 1,000 piercing damage."),
            Raw(103, "Alas, " + Located + " has died from pain and suffering."),
        ];
        foreach (var raw in lines)
        {
            Assert.True(LogLine.TryParse(raw, out var line), raw);
            processor.Process(line);
        }
        engine.EndCombat();

        var fight = engine.History[^1];
        Assert.Equal(1, fight.Combatants["DOOMCOIL"].Deaths);
        Assert.DoesNotContain(fight.Combatants.Keys, name => name.Contains(" IN ", StringComparison.Ordinal));
        Assert.Equal(SuccessLevel.Win, fight.GetSuccessLevel());
    }
}
