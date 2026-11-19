using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VESPER;
using Vesper.Setup;

namespace Vesper.Setup.Tests;

public sealed class InstallTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vesper-install-tests-").FullName;
    private readonly Registration _registration = new();
    private InstallPaths Paths => new(Path.Combine(_root, "Programs"), Path.Combine(_root, "VESPER"), Path.Combine(_root, "StartMenu"));

    [Fact]
    public void InstallRepairAndUninstallKeepUserFiles()
    {
        Directory.CreateDirectory(Paths.Data);
        string data = Path.Combine(Paths.Data, "settings.json");
        string history = Path.Combine(Paths.Data, "vesper.db");
        string subscriptions = Path.Combine(Paths.Data, "podcast-feeds.json");
        string media = Path.Combine(Paths.Data, "downloaded.mp4");
        File.WriteAllText(data, "user settings");
        File.WriteAllText(history, "user history");
        File.WriteAllText(subscriptions, "user subscriptions");
        File.WriteAllText(media, "downloaded media");
        var setup = new InstallTransaction(Paths, _registration);
        string source = SetupSource();
        RunPackage(setup, SetupMode.Install, source);
        Assert.Equal("1.2.3", _registration.Version);
        Assert.True(File.Exists(Paths.Host));
        Assert.Equal("setup", File.ReadAllText(Paths.Setup));
        File.Delete(Paths.Host);
        RunPackage(setup, SetupMode.Repair, source);
        Assert.True(File.Exists(Paths.Host));
        setup.Run(SetupMode.Uninstall, null, null, null, CancellationToken.None);
        Assert.Null(_registration.Version);
        Assert.False(Directory.Exists(Paths.App));
        Assert.Equal("user settings", File.ReadAllText(data));
        Assert.Equal("user history", File.ReadAllText(history));
        Assert.Equal("user subscriptions", File.ReadAllText(subscriptions));
        Assert.Equal("downloaded media", File.ReadAllText(media));
        Assert.False(File.Exists(Paths.Journal));
    }

    [Fact]
    public void OptInUninstallDeletesOnlySettingsHistoryAndSubscriptionFiles()
    {
        ExistingInstallation();
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(Path.Combine(Paths.Data, "settings.json"), "settings");
        File.WriteAllText(Path.Combine(Paths.Data, "settings.json.bad.1"), "old settings");
        File.WriteAllText(Path.Combine(Paths.Data, "ui-preview-settings.json"), "legacy settings");
        File.WriteAllText(Path.Combine(Paths.Data, "podcast-feeds.json"), "subscriptions");
        File.WriteAllText(Path.Combine(Paths.Data, "vesper.db"), "history");
        File.WriteAllText(Path.Combine(Paths.Data, "vesper.db-wal"), "history journal");
        File.WriteAllText(Path.Combine(Paths.Data, "vesper.db.bak-v8"), "history backup");
        File.WriteAllText(Path.Combine(Paths.Data, "vesper.db.bak-v8-20261119120000-1234abcd"), "older history backup");
        File.WriteAllText(Path.Combine(Paths.Data, "vesper.db.corrupt-20261119120000-00112233445566778899aabbccddeeff"), "corrupt history backup");
        File.WriteAllText(Path.Combine(Paths.Data, "notes.txt"), "unrelated data");
        string media = Path.Combine(Paths.Data, "downloaded.mp4");
        File.WriteAllText(media, "downloaded media");
        string nestedMedia = Path.Combine(Paths.Data, "Downloads", "movie.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedMedia)!);
        File.WriteAllText(nestedMedia, "another downloaded file");

        new InstallTransaction(Paths, _registration).Run(SetupMode.Uninstall, null, null, null,
            CancellationToken.None, deleteUserData: true);

        Assert.False(File.Exists(Path.Combine(Paths.Data, "settings.json")));
        Assert.False(File.Exists(Path.Combine(Paths.Data, "settings.json.bad.1")));
        Assert.False(File.Exists(Path.Combine(Paths.Data, "ui-preview-settings.json")));
        Assert.False(File.Exists(Path.Combine(Paths.Data, "podcast-feeds.json")));
        Assert.False(File.Exists(Path.Combine(Paths.Data, "vesper.db")));
        Assert.False(File.Exists(Path.Combine(Paths.Data, "vesper.db-wal")));
        Assert.False(File.Exists(Path.Combine(Paths.Data, "vesper.db.bak-v8")));
        Assert.False(File.Exists(Path.Combine(Paths.Data, "vesper.db.bak-v8-20261119120000-1234abcd")));
        Assert.False(File.Exists(Path.Combine(Paths.Data, "vesper.db.corrupt-20261119120000-00112233445566778899aabbccddeeff")));
        Assert.Equal("unrelated data", File.ReadAllText(Path.Combine(Paths.Data, "notes.txt")));
        Assert.Equal("downloaded media", File.ReadAllText(media));
        Assert.Equal("another downloaded file", File.ReadAllText(nestedMedia));
    }

    [Fact]
    public void OptInUninstallRestoresSettingsHistoryAndSubscriptionsWhenCancelled()
    {
        ExistingInstallation();
        Directory.CreateDirectory(Paths.Data);
        string settings = Path.Combine(Paths.Data, "settings.json");
        string history = Path.Combine(Paths.Data, "vesper.db");
        string subscriptions = Path.Combine(Paths.Data, "podcast-feeds.json");
        File.WriteAllText(settings, "settings");
        File.WriteAllText(history, "history");
        File.WriteAllText(subscriptions, "subscriptions");
        using var cancellation = new CancellationTokenSource();
        _registration.AfterUninstall = cancellation.Cancel;

        Assert.ThrowsAny<OperationCanceledException>(() => new InstallTransaction(Paths, _registration).Run(
            SetupMode.Uninstall, null, null, null, cancellation.Token, deleteUserData: true));

        Assert.Equal("old app", File.ReadAllText(Paths.Executable));
        Assert.Equal("1.0.0", _registration.Version);
        Assert.Equal("settings", File.ReadAllText(settings));
        Assert.Equal("history", File.ReadAllText(history));
        Assert.Equal("subscriptions", File.ReadAllText(subscriptions));
        Assert.Empty(Directory.EnumerateDirectories(Paths.Data));
        Assert.False(File.Exists(Paths.Journal));
    }

    [Fact]
    public void OptInUninstallRejectsADataRootOutsideTheUserStore()
    {
        ExistingInstallation();
        string outsideRoot = Path.Combine(_root, "Outside");
        Directory.CreateDirectory(outsideRoot);
        string outside = Path.Combine(outsideRoot, "settings.json");
        File.WriteAllText(outside, "leave this file alone");
        var unsafePaths = Paths with { Data = outsideRoot };

        Assert.Throws<IOException>(() => new InstallTransaction(unsafePaths, _registration).Run(
            SetupMode.Uninstall, null, null, null, CancellationToken.None, deleteUserData: true));

        Assert.Equal("old app", File.ReadAllText(Paths.Executable));
        Assert.Equal("1.0.0", _registration.Version);
        Assert.Equal("leave this file alone", File.ReadAllText(outside));
    }

    [Fact]
    public void NextAttemptFinishesOptInDataDeletionAfterCommittedUninstall()
    {
        Directory.CreateDirectory(Paths.Programs);
        Guid id = Guid.NewGuid();
        string backup = Paths.App + $".rollback-{id:N}";
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(Paths.Data);
        string dataStage = Path.Combine(Paths.Data, ".vesper-uninstall-" + id.ToString("N"));
        Directory.CreateDirectory(dataStage);
        File.WriteAllText(Path.Combine(dataStage, "settings.json"), "settings");
        File.WriteAllText(Path.Combine(dataStage, "vesper.db"), "history");
        InstallTransaction.WriteAtomic(Paths.Journal, JsonSerializer.SerializeToUtf8Bytes(
            new InstallJournal(id, SetupMode.Uninstall, InstallPhase.Committed, true, DeleteUserData: true)));

        Assert.ThrowsAny<OperationCanceledException>(() => new InstallTransaction(Paths, _registration).Run(
            SetupMode.Uninstall, null, null, null, new CancellationToken(canceled: true)));

        Assert.False(Directory.Exists(backup));
        Assert.False(Directory.Exists(dataStage));
        Assert.False(File.Exists(Path.Combine(Paths.Data, "settings.json")));
        Assert.False(File.Exists(Path.Combine(Paths.Data, "vesper.db")));
        Assert.False(File.Exists(Paths.Journal));
    }

    [Fact]
    public void FailureAfterSwapRestoresPreviousAppAndRegistration()
    {
        ExistingInstallation();
        _registration.AfterRegister = () => throw new IOException("shortcut could not be written");
        var setup = new InstallTransaction(Paths, _registration);
        Assert.Throws<IOException>(() => RunPackage(setup, SetupMode.Install, SetupSource()));
        Assert.Equal("old app", File.ReadAllText(Paths.Executable));
        Assert.Equal("1.0.0", _registration.Version);
        Assert.False(File.Exists(Paths.Journal));
        Assert.Single(Directory.EnumerateDirectories(Paths.Programs));
    }

    [Fact]
    public void CancellationDuringSwitchRollsBack()
    {
        ExistingInstallation();
        using var ct = new CancellationTokenSource();
        _registration.AfterRegister = ct.Cancel;
        var setup = new InstallTransaction(Paths, _registration);
        Assert.ThrowsAny<OperationCanceledException>(() => RunPackage(setup, SetupMode.Install, SetupSource(), ct.Token));
        Assert.Equal("old app", File.ReadAllText(Paths.Executable));
        Assert.Equal("1.0.0", _registration.Version);
    }

    [Fact]
    public void NextAttemptRecoversInterruptedUpgradeBeforeNewWork()
    {
        ExistingInstallation();
        var id = Guid.NewGuid();
        var journal = new InstallJournal(id, SetupMode.Install, InstallPhase.Switching, true, _registration.Capture());
        InstallTransaction.WriteAtomic(Paths.Journal, JsonSerializer.SerializeToUtf8Bytes(journal));
        Directory.Move(Paths.App, Paths.App + $".rollback-{id:N}");
        Directory.CreateDirectory(Paths.App);
        File.WriteAllBytes(Path.Combine(Paths.App, InstallPayload.Marker), id.ToByteArray());
        File.WriteAllText(Paths.Executable, "interrupted app");
        _registration.Version = "incomplete";
        var setup = new InstallTransaction(Paths, _registration);
        Assert.ThrowsAny<OperationCanceledException>(() => setup.Run(SetupMode.Repair, null, null, null, new CancellationToken(true)));
        Assert.Equal("old app", File.ReadAllText(Paths.Executable));
        Assert.Equal("1.0.0", _registration.Version);
        Assert.False(File.Exists(Paths.Journal));
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    [InlineData("missing host")]
    [InlineData("foreign extension")]
    [InlineData("link")]
    public void RejectsBadPayloadBeforeReplacingApp(string failure)
    {
        ExistingInstallation();
        var setup = new InstallTransaction(Paths, _registration);
        using var archive = new MemoryStream();
        var payload = Package(archive, failure);
        Assert.Throws<InvalidDataException>(() => setup.Run(SetupMode.Install, payload, SetupSource(), null, CancellationToken.None));
        Assert.Equal("old app", File.ReadAllText(Paths.Executable));
        Assert.Equal("1.0.0", _registration.Version);
        Assert.Single(Directory.EnumerateDirectories(Paths.Programs));
        Assert.False(File.Exists(Paths.Journal));
    }

    private void ExistingInstallation()
    {
        Directory.CreateDirectory(Paths.App);
        File.WriteAllText(Paths.Executable, "old app");
        _registration.Version = "1.0.0";
    }

    private string SetupSource()
    {
        string source = Path.Combine(_root, "setup.exe");
        File.WriteAllText(source, "setup");
        return source;
    }

    private static void RunPackage(InstallTransaction transaction, SetupMode mode, string source, CancellationToken ct = default)
    {
        using var archive = new MemoryStream();
        transaction.Run(mode, Package(archive), source, null, ct);
    }

    private static InstallPayload Package(MemoryStream archive, string? failure = null)
    {
        var entries = new List<(string Path, byte[] Bytes)>
        {
            ("VESPER.exe", Encoding.UTF8.GetBytes("new app")),
            ("extension/manifest.json", failure == "foreign extension" ? "{\"key\":\"YQ==\"}"u8.ToArray()
                : File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ChromeManifest.json")))
        };
        if (failure != "missing host") entries.Add(("vesper-host.exe", "host"u8.ToArray()));
        if (failure == "traversal") entries.Add(("extension/../other.txt", "bad"u8.ToArray()));
        if (failure == "duplicate") entries.Add(("vesper-HOST.exe", "bad"u8.ToArray()));
        var manifest = new PayloadManifest("1.2.3", entries.Select(file => new PayloadFile(file.Path, file.Bytes.Length,
            Convert.ToHexString(SHA256.HashData(file.Bytes)))).ToArray());
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in entries)
            {
                var entry = zip.CreateEntry(file.Path);
                if (failure == "link" && file.Path == "vesper-host.exe") entry.ExternalAttributes = unchecked((int)0xA0000000);
                using Stream stream = entry.Open();
                stream.Write(failure == "tampered" && file.Path == "VESPER.exe" ? "bad app"u8 : file.Bytes);
            }
        }
        archive.Position = 0;
        return new(archive, manifest);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Registration : IInstallRegistration
    {
        public string? Version { get; set; }
        public Action? AfterRegister { get; set; }
        public Action? AfterUninstall { get; set; }
        public RegistrationSnapshot Capture() => new(Version is null ? null : Encoding.UTF8.GetBytes(Version), null,
            Enumerable.Range(0, NativeHostRegistration.BrowserKeys.Length + 9)
                .Select(_ => new SavedValue(Microsoft.Win32.RegistryValueKind.Unknown, null)).ToArray());
        public void Install(string version)
        {
            Version = version;
            AfterRegister?.Invoke();
        }
        public void Uninstall()
        {
            Version = null;
            AfterUninstall?.Invoke();
        }
        public void Restore(RegistrationSnapshot snapshot) => Version = snapshot.Manifest is null ? null : Encoding.UTF8.GetString(snapshot.Manifest);
    }
}
