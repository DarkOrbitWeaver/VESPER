using Vesper.Engine;
using VESPER;

namespace Vesper.Tests;

public sealed class ClipSelectionTests
{
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    [Fact]
    public void A_valid_range_is_returned_in_seconds()
    {
        Assert.True(ClipSelection.TryGetRange("01:10", "0:02:30", TenMinutes, out double start, out double end, out string error));
        Assert.Equal(70, start);
        Assert.Equal(150, end);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData("abc", "02:00", "mm:ss")]
    [InlineData("", "02:00", "mm:ss")]
    [InlineData("02:00", "02:00", "later than the From time")]
    [InlineData("03:00", "02:00", "later than the From time")]
    [InlineData("00:00", "10:01", "past the end")]
    public void An_invalid_range_says_what_to_change(string from, string to, string expected)
    {
        Assert.False(ClipSelection.TryGetRange(from, to, TenMinutes, out _, out _, out string error));
        Assert.Contains(expected, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unknown_length_never_yields_a_range()
    {
        Assert.False(ClipSelection.TryGetRange("00:00", "01:00", null, out _, out _, out string error));
        Assert.Contains("known length", error);
    }

    [Fact]
    public void The_reason_explains_why_a_time_range_is_unavailable()
    {
        Assert.Null(ClipSelection.UnavailableReason(false, TenMinutes, ClipMode.Segments));
        Assert.Null(ClipSelection.UnavailableReason(false, TenMinutes, ClipMode.FullFile));
        Assert.Contains("live", ClipSelection.UnavailableReason(true, TenMinutes, ClipMode.Segments));
        Assert.Contains("known length", ClipSelection.UnavailableReason(false, null, ClipMode.Segments));
        Assert.Contains("known length", ClipSelection.UnavailableReason(false, TimeSpan.Zero, ClipMode.Segments));
        Assert.Contains("can't be cut", ClipSelection.UnavailableReason(false, TenMinutes, ClipMode.None));
    }

    [Fact]
    public void The_notice_matches_how_the_range_is_actually_cut()
    {
        Assert.Equal(ClipMode.FullFile, ClipSelection.ModeFor(MediaOutput.Wav, JobKind.Http));
        Assert.Equal(ClipMode.Segments, ClipSelection.ModeFor(MediaOutput.Wav, JobKind.Hls));
        Assert.Equal(ClipMode.Segments, ClipSelection.ModeFor(MediaOutput.Original, JobKind.Merge));
        Assert.Equal("Your clip may start a little early or end a little late.", ClipSelection.Notice(ClipMode.Segments));
        Assert.Contains("whole file first", ClipSelection.Notice(ClipMode.FullFile));
    }

    [Theory]
    [InlineData(92.7)]
    [InlineData(0.25)]
    public void The_end_handle_yields_a_valid_range_for_fractional_durations(double seconds)
    {
        TimeSpan duration = TimeSpan.FromSeconds(seconds);
        double end = ClipSelection.SecondsAt(308, 8, 300, duration.TotalSeconds);

        Assert.True(ClipSelection.TryGetRange("00:00", ClipSelection.TimeText(end), duration,
            out double start, out double parsedEnd, out string error), error);
        Assert.Equal(0, start);
        Assert.Equal(duration.TotalSeconds, parsedEnd);
    }

    [Fact]
    public void Bar_positions_and_times_round_trip()
    {
        const int left = 8, width = 300;
        const double duration = 600;
        Assert.Equal(0, ClipSelection.SecondsAt(-50, left, width, duration));
        Assert.Equal(600, ClipSelection.SecondsAt(999, left, width, duration));
        Assert.Equal(300, ClipSelection.SecondsAt(left + 150, left, width, duration));
        Assert.Equal(left + 150, ClipSelection.PixelAt(300, left, width, duration));
        Assert.Equal(left, ClipSelection.PixelAt(10, left, width, 0));
        Assert.Equal(0, ClipSelection.SecondsAt(40, left, 0, duration));
    }

    [Fact]
    public void Handles_cannot_cross_and_keep_one_second_apart()
    {
        Assert.Equal((99, 100), ClipSelection.MoveHandle(true, 500, 10, 100, 600));
        Assert.Equal((10, 11), ClipSelection.MoveHandle(false, 0, 10, 100, 600));
        Assert.Equal((0, 100), ClipSelection.MoveHandle(true, -5, 10, 100, 600));
        Assert.Equal((10, 600), ClipSelection.MoveHandle(false, 900, 10, 100, 600));
    }

    [Fact]
    public void Times_and_the_summary_read_as_clock_text()
    {
        Assert.Equal("00:01:10", ClipSelection.TimeText(70));
        Assert.Equal("1.01:00:00", ClipSelection.TimeText(TimeSpan.FromHours(25)));
        Assert.Equal("00:01:20 selected of 00:10:00", ClipSelection.Summary(70, 150, 600));
        Assert.True(ClipSelection.TryParseTime(ClipSelection.TimeText(70), out TimeSpan parsed));
        Assert.Equal(70, parsed.TotalSeconds);
    }
}
