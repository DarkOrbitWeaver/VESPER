using VESPER;

namespace VESPER.Tests;

public sealed class MeteredNetworkSettingsTests
{
    [Fact]
    public void Pause_on_metered_preference_defaults_off_and_roundtrips()
    {
        string root = Path.Combine(Path.GetTempPath(), "vesper-metered-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(SettingsStore.Load(root).PauseOnMeteredNetwork);

            var preferences = new VesperPreferences { PauseOnMeteredNetwork = true };
            Assert.Null(SettingsStore.Save(preferences, root));

            Assert.True(SettingsStore.Load(root).PauseOnMeteredNetwork);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void Copy_preferences_includes_metered_network_policy()
    {
        var source = new VesperPreferences { PauseOnMeteredNetwork = true };
        var destination = new VesperPreferences();

        Form1.CopyPreferences(source, destination);

        Assert.True(destination.PauseOnMeteredNetwork);
    }
}
