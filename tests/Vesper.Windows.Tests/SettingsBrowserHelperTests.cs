using System.Runtime.ExceptionServices;
using VESPER;

namespace Vesper.Windows.Tests;

public sealed class SettingsBrowserHelperTests
{
    [Fact]
    public void BrowserActionsFollowRegistrationChanges()
    {
        OnSta(() =>
        {
            using var repair = new VesperButton();
            using var disconnect = new VesperButton();

            SettingsForm.ApplyBrowserHelperState(repair, disconnect, hostPresent: true, registered: false);

            Assert.Equal("Connect Chrome", repair.Text);
            Assert.True(repair.Enabled);
            Assert.False(disconnect.Enabled);

            SettingsForm.ApplyBrowserHelperState(repair, disconnect, hostPresent: true, registered: true);

            Assert.Equal("Repair helper", repair.Text);
            Assert.True(repair.Enabled);
            Assert.True(disconnect.Enabled);

            SettingsForm.ApplyBrowserHelperState(repair, disconnect, hostPresent: false, registered: false);

            Assert.Equal("Connect Chrome", repair.Text);
            Assert.False(repair.Enabled);
            Assert.False(disconnect.Enabled);
        });
    }

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
        Assert.True(thread.Join(TimeSpan.FromSeconds(8)), "The settings test did not finish.");
        error?.Throw();
    }
}
