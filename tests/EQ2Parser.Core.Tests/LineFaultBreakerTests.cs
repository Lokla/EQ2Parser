using EQ2Parser.Core.Engine;

namespace EQ2Parser.Core.Tests;

/// <summary>The skip-or-stop rule behind per-line fault isolation: one bad
/// line is skipped, only an UNBROKEN run of failures stops a source, and
/// the journal breadcrumbs stay bounded however many lines fail.</summary>
public class LineFaultBreakerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    [Fact]
    public void A_Single_Failure_Is_Skipped_Not_Fatal()
    {
        var breaker = new LineFaultBreaker();
        var boom = new InvalidOperationException("boom");

        var outcome = breaker.RecordFailure(boom, T0);

        Assert.False(outcome.Stop);
        Assert.Equal(1, outcome.Skipped);
        Assert.Equal(1, outcome.Consecutive);
        Assert.False(breaker.Tripped);
        Assert.Equal(1, breaker.SkippedLines);
        Assert.Same(boom, breaker.LastError);
    }

    [Fact]
    public void A_Clean_Line_Ends_The_Run_But_Keeps_The_Total_And_The_Reason()
    {
        var breaker = new LineFaultBreaker(tripAfter: 3);
        var boom = new InvalidOperationException("boom");
        breaker.RecordFailure(boom, T0);
        breaker.RecordFailure(boom, T0);

        breaker.RecordSuccess();

        Assert.Equal(0, breaker.ConsecutiveFailures);
        // The Sources page still needs "2 skipped (last: …)" after recovery.
        Assert.Equal(2, breaker.SkippedLines);
        Assert.Same(boom, breaker.LastError);
        // …and the run starts over: two more are not three in a row.
        Assert.False(breaker.RecordFailure(boom, T0).Stop);
        Assert.False(breaker.RecordFailure(boom, T0).Stop);
        Assert.True(breaker.RecordFailure(boom, T0).Stop);
    }

    [Fact]
    public void Trips_Exactly_At_The_Threshold_Of_Consecutive_Failures()
    {
        var breaker = new LineFaultBreaker(tripAfter: 5);
        for (var i = 1; i < 5; i++)
            Assert.False(breaker.RecordFailure(new FormatException(), T0).Stop);

        var last = breaker.RecordFailure(new FormatException(), T0);

        Assert.True(last.Stop);
        Assert.True(breaker.Tripped);
        Assert.Equal(5, last.Consecutive);
        Assert.Equal(5, last.Skipped);
    }

    [Fact]
    public void Failures_That_Alternate_With_Good_Lines_Never_Trip()
    {
        // A fault on every OTHER line is bad data, not a dead pipeline —
        // the lines in between prove processing works.
        var breaker = new LineFaultBreaker(tripAfter: 3);
        for (var i = 0; i < 1000; i++)
        {
            Assert.False(breaker.RecordFailure(new FormatException(), T0).Stop);
            breaker.RecordSuccess();
        }
        Assert.False(breaker.Tripped);
        Assert.Equal(1000, breaker.SkippedLines);
    }

    [Fact]
    public void The_Default_Threshold_Clears_A_Raid_Wide_Block_Of_One_Bad_Shape()
    {
        // One AE writes the same line shape once per target, back to back:
        // 24 players and their pets, twice over, must not stop the source.
        var breaker = new LineFaultBreaker();
        for (var i = 0; i < 96; i++)
            Assert.False(breaker.RecordFailure(new FormatException(), T0).Stop);
        Assert.Equal(200, LineFaultBreaker.DefaultTripAfter);
    }

    [Fact]
    public void Journal_Breadcrumbs_Are_Throttled_But_Never_Lose_The_Count()
    {
        var breaker = new LineFaultBreaker(tripAfter: 1000);

        // The first failure always leaves a breadcrumb…
        Assert.True(breaker.RecordFailure(new FormatException(), T0).Journal);
        // …a storm right behind it does not…
        for (var i = 0; i < 50; i++)
        {
            Assert.False(breaker.RecordFailure(new FormatException(), T0.AddSeconds(1)).Journal);
            breaker.RecordSuccess();
        }
        // …and the next one due carries the running total, so the 50
        // suppressed lines are still accounted for.
        var due = breaker.RecordFailure(new FormatException(), T0 + LineFaultBreaker.JournalInterval);
        Assert.True(due.Journal);
        Assert.Equal(52, due.Skipped);
    }

    [Fact]
    public void The_Failure_That_Trips_Is_Always_Journaled()
    {
        var breaker = new LineFaultBreaker(tripAfter: 3);
        Assert.True(breaker.RecordFailure(new FormatException(), T0).Journal);
        Assert.False(breaker.RecordFailure(new FormatException(), T0).Journal);

        var tripping = breaker.RecordFailure(new FormatException(), T0);

        Assert.True(tripping.Stop);
        Assert.True(tripping.Journal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_Non_Positive_Threshold_Is_Refused(int tripAfter) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new LineFaultBreaker(tripAfter));
}
