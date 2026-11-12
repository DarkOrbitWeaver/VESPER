using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vesper.Setup;

namespace Vesper.Setup.Tests;

public sealed class InstallTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vesper-install-tests-").FullName;
    private readonly Registration _registration = new();
    private InstallPaths Paths => new(Path.Combine(_root, "Programs"), Path.Combine(_root, "Data"), Path.Combine(_root, "StartMenu"));

    [Fact]
    public void InstallRepairAndUninstallKeepUserFiles()
    {
        Directory.CreateDirectory(Paths.Data);
        string data = Path.Combine(Paths.Data, "settings.json");
        File.WriteAllText(data, "user settings");
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
        public RegistrationSnapshot Capture() => new(Version is null ? null : Encoding.UTF8.GetBytes(Version), null, []);
        public void Install(string version)
        {
            Version = version;
            AfterRegister?.Invoke();
        }
        public void Uninstall() => Version = null;
        public void Restore(RegistrationSnapshot snapshot) => Version = snapshot.Manifest is null ? null : Encoding.UTF8.GetString(snapshot.Manifest);
    }
}
