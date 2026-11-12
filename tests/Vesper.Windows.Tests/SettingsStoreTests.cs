using VESPER;

namespace Vesper.Windows.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vesper-settings-tests-").FullName;

    [Fact]
    public void TestDataDirectoryOverrideIsResolvedWithoutChangingTheDefaultPath()
    {
        const string variable = "VESPER_TEST_DATA_DIRECTORY";
        string? previous = Environment.GetEnvironmentVariable(variable);
        string path = Path.Combine(_root, "isolated-data");
        try
        {
            Environment.SetEnvironmentVariable(variable, path);
            Assert.Equal(Path.GetFullPath(path), EngineHost.DataDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void CorruptSettingsAreKeptInNumberedBackups()
    {
        string path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, "{ first invalid settings }");

        Assert.Equal(ThemeChoice.Light, SettingsStore.Load(_root).Theme);
        Assert.Equal("{ first invalid settings }", File.ReadAllText(path + ".bad"));

        File.WriteAllText(path, "{ second invalid settings }");
        Assert.Equal(ThemeChoice.Light, SettingsStore.Load(_root).Theme);

        Assert.Equal("{ first invalid settings }", File.ReadAllText(path + ".bad"));
        Assert.Equal("{ second invalid settings }", File.ReadAllText(path + ".bad.1"));
    }

    [Fact]
    public void FailedReplaceLeavesPreviousSettingsIntact()
    {
        var original = new VesperPreferences { DefaultFolder = "original folder" };
        Assert.Null(SettingsStore.Save(original, _root));
        string path = Path.Combine(_root, "settings.json");
        string previous = File.ReadAllText(path);

        string? error;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            error = SettingsStore.Save(new VesperPreferences { DefaultFolder = "new folder" }, _root);

        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(previous, File.ReadAllText(path));
        Assert.Equal("original folder", SettingsStore.Load(_root).DefaultFolder);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
