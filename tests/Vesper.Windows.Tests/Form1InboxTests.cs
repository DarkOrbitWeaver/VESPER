using System.Reflection;
using System.Runtime.ExceptionServices;
using VESPER;
using Vesper.Engine;

namespace Vesper.Windows.Tests;

[Collection("Main window UI")]
public sealed class Form1InboxTests
{
    [Fact]
    public void EngineUpdateArrivingBeforeFirstHandleIsNotLost()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form1();
                Assert.False(form.IsHandleCreated, "This test must enqueue before Windows creates the form handle.");

                Guid id = Guid.NewGuid();
                var snapshot = new JobSnapshot { Id = id, FileName = "early-download.zip", State = JobState.Queued };
                var enqueue = typeof(Form1).GetMethod("Enqueue", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var items = (Dictionary<Guid, DownloadItem>)typeof(Form1)
                    .GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

                enqueue.Invoke(form, [snapshot]);
                Assert.False(items.ContainsKey(id), "The UI cannot process the inbox before its handle exists.");

                _ = form.Handle;
                Assert.True(items.TryGetValue(id, out DownloadItem? item),
                    "Creating the UI handle must flush earlier engine updates without another event.");
                Assert.Equal("early-download.zip", item!.Name);
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The window lifecycle test did not complete.");
        failure?.Throw();
    }
}
