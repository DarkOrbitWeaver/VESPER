using System.Diagnostics;
using System.Runtime.ExceptionServices;
using VESPER;
using Vesper.Engine;
using Vesper.Shared;

namespace Vesper.Windows.Tests;

public sealed class SetupExitHandoffTests
{
    [Fact]
    public void SetupExitCommandIsNotAnExtensionBridgeRequest()
    {
        string json = SetupExitProtocol.CreateRequest(Guid.NewGuid().ToString("N"));

        Assert.False(BridgeProtocol.TryParse(json, out _, out _));
    }

    [Fact]
    public void IdleInstanceAcceptsSetupExitWithoutDownloadPrompt()
    {
        HandoffResult result = OnSta(() => SendRequest(hasActiveDownloads: false, DialogResult.No));

        Assert.True(result.Accepted);
        Assert.True(result.FormClosed);
        Assert.Equal(0, result.ConfirmationCount);
    }

    [Theory]
    [InlineData(DialogResult.Yes, true, true)]
    [InlineData(DialogResult.No, false, false)]
    public void ActiveDownloadsUseTheExistingExitConfirmation(
        DialogResult choice, bool expectedAccepted, bool expectedClosed)
    {
        OnSta(() =>
        {
            HandoffResult result = SendRequest(hasActiveDownloads: true, choice);

            Assert.Equal(expectedAccepted, result.Accepted);
            Assert.Equal(expectedClosed, result.FormClosed);
            Assert.Equal(1, result.ConfirmationCount);
            return 0;
        });
    }

    [Fact]
    public void CancelledSetupRequestDoesNotCloseInstanceWhenPromptIsLaterAccepted()
    {
        OnSta(() =>
        {
            using var promptShown = new ManualResetEventSlim();
            using var answerPrompt = new ManualResetEventSlim();
            using var cancelRequest = new CancellationTokenSource();
            string pipeName = SetupExitProtocol.PipeNameForCurrentUser() + "-test-" + Guid.NewGuid().ToString("N");
            int confirmationCount = 0;
            using var form = new Form1(new VesperPreferences(), null, startHidden: true,
                hasActiveDownloads: () => true,
                confirmExitWithDownloads: () =>
                {
                    confirmationCount++;
                    promptShown.Set();
                    answerPrompt.Wait(TimeSpan.FromSeconds(5));
                    return DialogResult.Yes;
                });
            _ = form.Handle;
            using var server = new BridgeServer(pipeName,
                (_, _) => Task.FromResult(BridgeResponse.Fail("unused-request", "invalid", "Not used.")),
                form.HandleSetupExitRequestAsync, form.SetupExitResponseWritten);
            server.Start();

            Task<bool> request = Task.Run(async () =>
            {
                try
                {
                    return await SetupExitProtocol.RequestExitAsync(pipeName, TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(3), cancelRequest.Token);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            });
            Task cancelAfterPrompt = Task.Run(() =>
            {
                if (promptShown.Wait(TimeSpan.FromSeconds(3)))
                {
                    cancelRequest.Cancel();
                    Thread.Sleep(100);
                }
                answerPrompt.Set();
            });

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                PumpUntil(request, timeout.Token);
                bool accepted = request.GetAwaiter().GetResult();
                PumpUntil(cancelAfterPrompt, timeout.Token);
                Application.DoEvents();

                Assert.False(accepted);
                Assert.False(form.IsDisposed);
                Assert.Equal(1, confirmationCount);
            }
            finally
            {
                answerPrompt.Set();
                cancelRequest.Cancel();
                cancelAfterPrompt.Wait(TimeSpan.FromSeconds(2));
            }
            return 0;
        });
    }

    private static HandoffResult SendRequest(bool hasActiveDownloads, DialogResult choice)
    {
        int confirmationCount = 0;
        string pipeName = SetupExitProtocol.PipeNameForCurrentUser() + "-test-" + Guid.NewGuid().ToString("N");
        using var form = new Form1(new VesperPreferences(), null, startHidden: true,
            hasActiveDownloads: () => hasActiveDownloads,
            confirmExitWithDownloads: () =>
            {
                confirmationCount++;
                return choice;
            });
        _ = form.Handle;
        using var server = new BridgeServer(pipeName,
            (_, _) => Task.FromResult(BridgeResponse.Fail("unused-request", "invalid", "Not used.")),
            form.HandleSetupExitRequestAsync, form.SetupExitResponseWritten);
        server.Start();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<bool> request = SetupExitProtocol.RequestExitAsync(pipeName, TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3), timeout.Token);
        PumpUntil(request, timeout.Token);
        bool accepted = request.GetAwaiter().GetResult();
        if (accepted) PumpUntilClosed(form, timeout.Token);
        else Application.DoEvents();

        return new(accepted, form.IsDisposed, confirmationCount);
    }

    private static void PumpUntil(Task task, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(4))
        {
            ct.ThrowIfCancellationRequested();
            Application.DoEvents();
            Thread.Sleep(5);
        }
        Assert.True(task.IsCompleted, "The setup exit pipe request did not finish.");
    }

    private static void PumpUntilClosed(Form form, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (!form.IsDisposed && watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            ct.ThrowIfCancellationRequested();
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private static T OnSta<T>(Func<T> action)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(8)), "The setup exit UI test timed out.");
        failure?.Throw();
        return result;
    }

    private sealed record HandoffResult(bool Accepted, bool FormClosed, int ConfirmationCount);
}
