using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Logs;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// A bug in an alert handler must never cost combat numbers. The processor
/// runs the live alert stages (triggers, trigger shares, raid lines, status
/// callouts, spell timers) in the same pass as the grammar, several of them
/// BEFORE it — so each stage is contained: a throw is counted and handed to
/// <see cref="LogLineProcessor.AlertFaulted"/>, and the line's damage still
/// lands.
/// </summary>
public class AlertStageContainmentTests
{
    private const long Epoch = 1_775_000_000;

    private static LogLine Line(string message, long secondsIn = 0)
    {
        Assert.True(LogLine.TryParse($"({Epoch + secondsIn})[s] {message}", out var line));
        return line;
    }

    private sealed class Harness
    {
        public ParserEngine Engine { get; } = new("log-a", "Tester");
        public TriggerEngine Triggers { get; } = new("Tester");
        public SpellTimerService Timers { get; } = new();
        public LogLineProcessor Processor { get; }
        public List<(AlertStage Stage, Exception Error)> Faults { get; } = [];

        public Harness()
        {
            Processor = new LogLineProcessor(Engine, Triggers, Timers);
            Processor.AlertFaulted += (stage, error) => Faults.Add((stage, error));
        }

        public long Damage => Engine.ActiveEncounter?.Combatants["TESTER"].Damage ?? 0;
    }

    [Fact]
    public void A_Throwing_Trigger_Handler_Does_Not_Cost_The_Lines_Damage()
    {
        var h = new Harness();
        h.Triggers.AddOrUpdate(new Trigger("Smite hits") { SoundType = TriggerSound.Beep });
        h.Triggers.Fired += static _ => throw new InvalidOperationException("audio device went away");

        // Before: this threw, the grammar never ran, and 500 damage was lost.
        h.Processor.Process(Line("YOUR Smite hits a gnoll for 500 divine damage."));

        Assert.Equal(500, h.Damage);
        Assert.Equal(1, h.Processor.LinesMatched);
        var (stage, error) = Assert.Single(h.Faults);
        Assert.Equal(AlertStage.Triggers, stage);
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(1, h.Processor.AlertFaults);
    }

    [Fact]
    public void A_Throwing_Raid_Line_Handler_Does_Not_Cost_The_Lines_Damage()
    {
        var h = new Harness();
        h.Processor.RaidLine += static (_, _) => throw new InvalidOperationException("roster rule broke");

        // A combat line the attendance prefilter also wants (" the raid").
        h.Processor.Process(Line("YOUR Smite hits Bane of the raid for 500 divine damage."));

        Assert.Equal(500, h.Damage);
        Assert.Equal(AlertStage.RaidLine, Assert.Single(h.Faults).Stage);
    }

    [Fact]
    public void A_Throwing_Trigger_Share_Handler_Does_Not_Stop_The_Line()
    {
        var h = new Harness();
        h.Processor.TriggerShared += static _ => throw new InvalidOperationException("prompt broke");
        var raidLines = 0;
        h.Processor.RaidLine += (_, _) => raidLines++;

        h.Processor.Process(Line("""\aPC -1 Helper:Helper\/a says to the raid party, "<Trigger R="a dangerous device appears" SD="Move out" ST="3" CR="F" C="General" T="F" TN="" Ta="F" />" """.TrimEnd()));

        Assert.Equal(AlertStage.TriggerShare, Assert.Single(h.Faults).Stage);
        // The stage AFTER the broken one still ran for this line.
        Assert.Equal(1, raidLines);
    }

    [Fact]
    public void One_Broken_Stage_Does_Not_Cost_The_Other_Stages_Of_The_Same_Line()
    {
        var h = new Harness();
        h.Triggers.AddOrUpdate(new Trigger("Smite hits") { SoundType = TriggerSound.Beep });
        h.Triggers.Fired += static _ => throw new InvalidOperationException("audio device went away");
        List<string> raidLines = [];
        h.Processor.RaidLine += (message, _) => raidLines.Add(message);

        h.Processor.Process(Line("YOUR Smite hits Bane of the raid for 500 divine damage."));

        // Triggers threw; the raid line and the swing both still happened.
        Assert.Equal(AlertStage.Triggers, Assert.Single(h.Faults).Stage);
        Assert.Single(raidLines);
        Assert.Equal(500, h.Damage);
    }

    [Fact]
    public void A_Throwing_Status_Callout_Handler_Does_Not_Cost_The_Status_Swing()
    {
        var h = new Harness();
        h.Processor.StatusApplied += static (_, _, _) => throw new InvalidOperationException("speech broke");
        h.Processor.Process(Line("YOUR Smite hits a gnoll for 500 divine damage."));

        h.Processor.Process(Line("Tester is stunned!", secondsIn: 1));

        Assert.Equal(AlertStage.StatusCallout, Assert.Single(h.Faults).Stage);
        Assert.Equal(2, h.Processor.LinesMatched);
        // The apply was still recorded into the live fight.
        Assert.Contains(
            h.Engine.ActiveEncounter!.Combatants.Values.SelectMany(c => c.IncomingBuckets.Values).SelectMany(b => b.All.Swings),
            s => s.Category == EQ2Parser.Core.Combat.SwingCategory.StatusEffect);
    }

    [Fact]
    public void A_Throwing_Timer_Handler_Does_Not_Fail_A_Line_That_Parsed()
    {
        var h = new Harness();
        h.Timers.AddOrUpdateDefinition(new TimerDefinition { Name = "Smite", DurationSeconds = 30 });
        h.Timers.TimerStarted += static (_, _) => throw new InvalidOperationException("overlay broke");

        // The swing is recorded before the timers hear of it — but the
        // throw used to surface as a failed LINE all the same, and failed
        // lines are what stop a source.
        h.Processor.Process(Line("YOUR Smite hits a gnoll for 500 divine damage."));
        h.Processor.Process(Line("YOUR Smite hits a gnoll for 500 divine damage.", secondsIn: 1));

        Assert.Equal(1000, h.Damage);
        Assert.All(h.Faults, f => Assert.Equal(AlertStage.SpellTimers, f.Stage));
        Assert.NotEmpty(h.Faults);
    }

    [Fact]
    public void A_Fault_Is_Counted_Even_With_Nobody_Listening_And_A_Broken_Listener_Changes_Nothing()
    {
        var engine = new ParserEngine("log-a", "Tester");
        var triggers = new TriggerEngine("Tester");
        triggers.AddOrUpdate(new Trigger("Smite hits") { SoundType = TriggerSound.Beep });
        triggers.Fired += static _ => throw new InvalidOperationException("audio device went away");
        var processor = new LogLineProcessor(engine, triggers);

        processor.Process(Line("YOUR Smite hits a gnoll for 500 divine damage."));
        Assert.Equal(1, processor.AlertFaults);

        // Reporting a fault must not become the thing that costs the line.
        processor.AlertFaulted += static (_, _) => throw new InvalidOperationException("the reporter broke too");
        processor.Process(Line("YOUR Smite hits a gnoll for 500 divine damage.", secondsIn: 1));

        Assert.Equal(2, processor.AlertFaults);
        Assert.Equal(1000, engine.ActiveEncounter!.Combatants["TESTER"].Damage);
    }

    [Fact]
    public void Replayed_History_Never_Reaches_The_Alert_Stages_So_It_Cannot_Fault_Them()
    {
        var h = new Harness();
        h.Triggers.AddOrUpdate(new Trigger("Smite hits") { SoundType = TriggerSound.Beep });
        h.Triggers.Fired += static _ => throw new InvalidOperationException("audio device went away");
        h.Processor.RaidLine += static (_, _) => throw new InvalidOperationException("roster rule broke");
        var line = Line("YOUR Smite hits Bane of the raid for 500 divine damage.");

        h.Processor.Process(line with { ObservedAt = line.Timestamp + LogLineProcessor.TriggerFreshness + TimeSpan.FromSeconds(1) });

        Assert.Empty(h.Faults);
        Assert.Equal(500, h.Damage);
    }

    [Fact]
    public void The_Combat_Path_Is_Not_Contained()
    {
        // Containment is for the alert side only. A fault in the combat
        // path (here a subscriber of the finished-fight announcement, which
        // the idle rule raises at the top of the next line) still
        // propagates: the caller must know the LINE failed.
        var h = new Harness();
        h.Engine.EncounterEnded += static _ => throw new InvalidOperationException("consumer broke");
        h.Processor.Process(Line("YOUR Smite hits a gnoll for 500 divine damage."));

        Assert.Throws<InvalidOperationException>(() =>
            h.Processor.Process(Line("YOUR Smite hits a gnoll for 500 divine damage.", secondsIn: 60)));

        Assert.Empty(h.Faults);
    }
}
