using System.Runtime.ExceptionServices;
using VESPER;
using Vesper.Engine;

namespace Vesper.Windows.Tests;

[Collection("Main window UI")]
public sealed class QueueDiskForecastTests
{
    [Fact]
    public void Known_direct_downloads_are_grouped_by_volume_and_subtract_saved_bytes()
    {
        JobSnapshot[] jobs =
        [
            Job("C:\\downloads", 1000, committed: 200),
            Job("C:\\other", 2000),
            Job("D:\\media", 10_000, output: MediaOutput.Wav, committed: 3000),
            Job("C:\\done", 9000, state: JobState.Completed)
        ];
        var available = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase)
        {
            ["C:\\"] = 5000,
            ["D:\\"] = 500
        };

        IReadOnlyList<DiskForecastVolume> result = QueueDiskForecast.Calculate(jobs, available, safetyReserveBytes: 100);

        DiskForecastVolume c = Assert.Single(result, volume => volume.Root == "C:\\");
        Assert.Equal(2800, c.KnownRemainingBytes);
        Assert.Equal(100, c.SafetyReserveBytes);
        Assert.Equal(2900, c.RequiredBytes);
        Assert.Equal(5000, c.AvailableBytes);
        Assert.False(c.IsShort);

        DiskForecastVolume d = Assert.Single(result, volume => volume.Root == "D:\\");
        Assert.Equal(0, d.KnownRemainingBytes);
        Assert.Equal(1, d.UnknownJobs);
        Assert.Equal(500, d.AvailableBytes);
    }

    [Fact]
    public void Forecast_marks_only_volumes_below_the_known_requirement()
    {
        JobSnapshot[] jobs = [Job("C:\\downloads", 1000)];
        IReadOnlyList<DiskForecastVolume> shortByOne = QueueDiskForecast.Calculate(jobs,
            new Dictionary<string, long?> { ["C:\\"] = 1099 }, 100);
        IReadOnlyList<DiskForecastVolume> enough = QueueDiskForecast.Calculate(jobs,
            new Dictionary<string, long?> { ["C:\\"] = 1100 }, 100);

        Assert.True(Assert.Single(shortByOne).IsShort);
        Assert.False(Assert.Single(enough).IsShort);
        Assert.Equal("Low disk space on C:", QueueDiskForecast.Summary(shortByOne));
        Assert.Contains("free for this queue", QueueDiskForecast.Details(shortByOne));
    }

    [Fact]
    public void Unknown_sizes_and_unavailable_free_space_do_not_signal_a_shortage()
    {
        JobSnapshot[] jobs =
        [
            Job("C:\\downloads", 1000),
            Job("C:\\downloads", 20_000, output: MediaOutput.Wav)
        ];
        IReadOnlyList<DiskForecastVolume> forecast = QueueDiskForecast.Calculate(jobs,
            new Dictionary<string, long?> { ["C:\\"] = null }, 64 * 1024 * 1024);

        DiskForecastVolume volume = Assert.Single(forecast);
        Assert.Equal(1, volume.UnknownJobs);
        Assert.False(volume.IsShort);
    }

    [Fact]
    public void Main_window_footer_keeps_unknown_sizes_out_of_the_way()
    {
        OnSta(() =>
        {
            using var form = new Form1();
            form.LoadPreviewRows([DownloadItem.From(Job("C:\\downloads", 20_000, output: MediaOutput.Wav))]);
            Label footer = Assert.IsAssignableFrom<Label>(Assert.Single(form.Controls.Find("statusText", searchAllChildren: true)));

            Assert.DoesNotContain("Disk", footer.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("", footer.AccessibleDescription);
            Assert.True(footer.AutoEllipsis);
        });
    }

    private static JobSnapshot Job(string destination, long? total, long committed = 0,
        MediaOutput output = MediaOutput.Original, JobState state = JobState.Queued) => new()
    {
        Id = Guid.NewGuid(),
        Kind = JobKind.Http,
        Output = output,
        Destination = destination,
        TotalBytes = total,
        CommittedBytes = committed,
        State = state
    };

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(8)), "The main window UI test did not finish.");
        error?.Throw();
    }
}
