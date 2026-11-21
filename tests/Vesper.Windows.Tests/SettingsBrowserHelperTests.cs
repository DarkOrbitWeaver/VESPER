using VESPER;

namespace Vesper.Windows.Tests;

public sealed class SettingsBrowserHelperTests
{
    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, false, false)]
    public void NormalInstalledViewHidesSetupActionsAndPortableCanDisconnect(
        bool hostPresent, bool registered, bool installed, bool expected) =>
        Assert.Equal(expected, SettingsForm.ShouldShowBrowserHelperAction(hostPresent, registered, installed));
}
