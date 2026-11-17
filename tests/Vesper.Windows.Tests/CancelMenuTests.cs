using System.ComponentModel;
using System.Reflection;
using System.Runtime.ExceptionServices;
using VESPER;
using Vesper.Engine;

namespace Vesper.Windows.Tests;

[Collection("Main window UI")]
public sealed class CancelMenuTests
{
    [Fact]
    public void UnfinishedDownloadsShowOnlyCancelNotRemoveFromList()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form1();
                var commands = (Dictionary<string, ToolStripMenuItem>)typeof(Form1)
                    .GetField("_menuCommands", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                var target = typeof(Form1).GetField("_menuTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var configure = typeof(Form1).GetMethod("ConfigureRowMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;

                target.SetValue(form, DownloadItem.From(new JobSnapshot
                {
                    Id = Guid.NewGuid(), FileName = "unfinished.zip", State = JobState.Downloading
                }));
                configure.Invoke(form, [null, new CancelEventArgs()]);
                Assert.True(commands["Cancel download"].Available);
                Assert.False(commands["Remove from list…"].Available);
                Assert.False(commands["Delete downloaded file…"].Available);

                target.SetValue(form, DownloadItem.From(new JobSnapshot
                {
                    Id = Guid.NewGuid(), FileName = "completed.zip", State = JobState.Completed, FinalPath = "completed.zip"
                }));
                configure.Invoke(form, [null, new CancelEventArgs()]);
                Assert.False(commands["Cancel download"].Available);
                Assert.True(commands["Remove from list…"].Available);
            }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        failure?.Throw();
    }
    [Fact]
    public void SelectionToolbarUsesCancelForUnfinishedAndRemoveForCompleted()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form1();
                var grid = (DownloadGrid)typeof(Form1)
                    .GetField("_grid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                var remove = (Control)typeof(Form1)
                    .GetField("_remove", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

                DownloadItem unfinished = DownloadItem.From(new JobSnapshot
                {
                    Id = Guid.NewGuid(), FileName = "partial.zip", State = JobState.Paused
                });
                DownloadItem completed = DownloadItem.From(new JobSnapshot
                {
                    Id = Guid.NewGuid(), FileName = "done.zip", State = JobState.Completed
                });

                grid.SetItems([unfinished]);
                grid.CheckAllVisible();
                Assert.True(remove.Enabled);
                Assert.Equal("Cancel", remove.Text);

                grid.ClearChecks();
                grid.SetItems([completed]);
                grid.CheckAllVisible();
                Assert.True(remove.Enabled);
                Assert.Equal("Remove", remove.Text);

                grid.ClearChecks();
                grid.SetItems([unfinished, completed]);
                grid.CheckAllVisible();
                Assert.False(remove.Enabled);
            }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        failure?.Throw();
    }
}
