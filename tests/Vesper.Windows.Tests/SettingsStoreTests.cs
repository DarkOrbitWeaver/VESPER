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
    public void LoadExpandsEnvironmentVariablesInTheDefaultDownloadFolder()
    {
        string variable = "VESPER_TEST_FOLDER_" + Guid.NewGuid().ToString("N");
        string? previous = Environment.GetEnvironmentVariable(variable);
        string root = Path.Combine(_root, "expanded-root");
        try
        {
            Environment.SetEnvironmentVariable(variable, root);
            string tokenPath = $"%{variable}%{Path.DirectorySeparatorChar}Downloads";
            Assert.Null(SettingsStore.Save(new VesperPreferences { DefaultFolder = tokenPath }, _root));

            Assert.Equal(Path.GetFullPath(Path.Combine(root, "Downloads")), SettingsStore.Load(_root).DefaultFolder);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void FilenameTemplateSurvivesSettingsSaveAndReload()
    {
        Assert.Null(SettingsStore.Save(new VesperPreferences { FileNameTemplate = "{name} - {host}" }, _root));

        Assert.Equal("{name} - {host}", SettingsStore.Load(_root).FileNameTemplate);
    }

    [Fact]
    public void OpeningAfterCompletionDefaultsOnAndCanBeDisabledSeparately()
    {
        Assert.True(SettingsStore.Load(_root).OpenVesperOnCompletion);
        Assert.Null(SettingsStore.Save(new VesperPreferences { OpenVesperOnCompletion = false, NotifyOnFinish = true }, _root));
        var saved = SettingsStore.Load(_root);
        Assert.False(saved.OpenVesperOnCompletion);
        Assert.True(saved.NotifyOnFinish);
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
