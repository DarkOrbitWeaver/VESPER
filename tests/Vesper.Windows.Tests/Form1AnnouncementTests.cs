using System.Reflection;
using System.Runtime.ExceptionServices;
using VESPER;

namespace Vesper.Windows.Tests;

[Collection("Main window UI")]
public sealed class Form1AnnouncementTests
{
    [Fact]
    public void NoticesStackAboveEachOtherWithinTheWorkArea()
    {
        using var newest = new VesperNotice();
        using var older = new VesperNotice();
        var area = new Rectangle(-900, 50, UiKit.Scale(newest, 800), UiKit.Scale(newest, 175));

        Assert.True(newest.PositionInStack(area, area.Bottom, first: true));
        Assert.True(older.PositionInStack(area, newest.Top, first: false));
        Assert.True(area.Contains(newest.Bounds));
        Assert.True(area.Contains(older.Bounds));
        Assert.True(older.Bottom < newest.Top);
        Assert.False(older.PositionInStack(area, older.Top, first: false));
    }

    [Fact]
    public void Screen_reader_throttle_keeps_the_most_recent_outcome()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form1();
                _ = form.Handle;

                MethodInfo announce = typeof(Form1).GetMethod("AnnounceToScreenReader",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                announce.Invoke(form,
                [
                    JobStatus.Downloading,
                    new DownloadItem { Name = "first.bin", Status = JobStatus.Failed }
                ]);
                announce.Invoke(form,
                [
                    JobStatus.Downloading,
                    new DownloadItem { Name = "second.bin", Status = JobStatus.NeedsAttention }
                ]);

                string? pending = (string?)typeof(Form1).GetField("_pendingAnnouncement",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
                Assert.Equal("second.bin needs attention", pending);
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The window announcement test did not complete.");
        failure?.Throw();
    }
}
