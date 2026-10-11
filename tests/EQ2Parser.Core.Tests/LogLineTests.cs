using EQ2Parser.Core.Logs;

namespace EQ2Parser.Core.Tests;

public class LogLineTests
{
    [Fact]
    public void Parses_A_Real_Line()
    {
        var raw = "(1753738000)[Mon Jul 28 22:26:40 2026] You hit a training dummy for 100 points of crushing damage.";
        Assert.True(LogLine.TryParse(raw, out var line));
        Assert.Equal(1753738000, line.Epoch);
        Assert.Equal("You hit a training dummy for 100 points of crushing damage.", line.Message);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1753738000), line.Timestamp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a log line")]
    [InlineData("(abc)[stamp] message")] // non-numeric epoch
    [InlineData("(123 no close paren")]
    [InlineData("(123)no bracket after")]
    [InlineData("(123)[unclosed stamp")]
    public void Rejects_Garbage_Without_Throwing(string raw)
    {
        Assert.False(LogLine.TryParse(raw, out _));
    }

    // An epoch that parses as a number but is no representable time used
    // to make Timestamp throw on first use, on the log-line path.
    [Theory]
    [InlineData("(999999999999999999)[x] boom")]
    [InlineData("(253402300800)[x] one second past the year 9999")]
    [InlineData("(-62135596801)[x] one second before the year 1")]
    [InlineData("(-9223372036854775808)[x] long.MinValue")]
    public void Rejects_An_Epoch_That_Is_Not_A_Representable_Time(string raw)
    {
        Assert.False(LogLine.TryParse(raw, out _));
    }

    [Theory]
    [InlineData("(253402300799)[x] the last representable second", 253402300799)]
    [InlineData("(-62135596800)[x] the first representable second", -62135596800)]
    [InlineData("(0)[x] the epoch itself", 0)]
    public void Accepts_Every_Epoch_Timestamp_Can_Represent(string raw, long epoch)
    {
        Assert.True(LogLine.TryParse(raw, out var line));
        Assert.Equal(epoch, line.Epoch);
        _ = line.Timestamp; // must not throw
    }

    [Fact]
    public void Rejects_Partial_Tail_Read()
    {
        // A live tail can hand us the front half of a line mid-write.
        Assert.False(LogLine.TryParse("(17537380", out _));
    }
}
