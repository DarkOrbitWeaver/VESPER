using System.Text.Json;
using Microsoft.Win32;
using VESPER;
using Vesper.Setup;

namespace Vesper.Setup.Tests;

public sealed class InstallTransactionJournalTests : IDisposable
{
    private const int AppValueCount = 9;
    private const int SnapshotValueCount = 3 + AppValueCount;
    private readonly string _root = Directory.CreateTempSubdirectory("vesper-setup-journal-").FullName;

    [Theory]
    [InlineData("null-array")]
    [InlineData("null-snapshot")]
    [InlineData("wrong-count")]
    [InlineData("too-many-values")]
    [InlineData("null-entry")]
    [InlineData("invalid-string")]
    [InlineData("invalid-null-string")]
    [InlineData("invalid-dword")]
    [InlineData("invalid-qword")]
    [InlineData("invalid-base64")]
    [InlineData("invalid-multistring")]
    [InlineData("value-with-unknown-kind")]
    [InlineData("undefined-kind")]
    [InlineData("oversized-registry-value")]
    [InlineData("oversized-registry-string")]
    [InlineData("oversized-manifest")]
    [InlineData("oversized-shortcut")]
    public void MalformedSwitchingSnapshotDoesNotChangeInterruptedSwap(string failure)
    {
        var paths = Paths();
        Guid id = PrepareInterruptedSwap(paths);
        var registration = new Registration { CurrentValue = "partially installed" };
        var journal = new InstallJournal(id, SetupMode.Repair, InstallPhase.Switching, true,
            MalformedSnapshot(failure));
        InstallTransaction.WriteAtomic(paths.Journal, JsonSerializer.SerializeToUtf8Bytes(journal));

        var error = Assert.Throws<InvalidDataException>(() =>
            new InstallTransaction(paths, registration).Run(SetupMode.Repair, null, null, null, CancellationToken.None));

        Assert.Equal("The setup recovery record is incomplete.", error.Message);
        Assert.Equal("new app", File.ReadAllText(paths.Executable));
        Assert.Equal(id.ToByteArray(), File.ReadAllBytes(Path.Combine(paths.App, InstallPayload.Marker)));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(Backup(paths, id), "VESPER.exe")));
        Assert.False(Directory.Exists(Stage(paths, id)));
        Assert.True(File.Exists(paths.Journal));
        Assert.Equal("partially installed", registration.CurrentValue);
        Assert.Equal(0, registration.RestoreCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidHistoricalSnapshotStillRestoresInterruptedSwap(bool populated)
    {
        var paths = Paths();
        Guid id = PrepareInterruptedSwap(paths);
        var registration = new Registration { CurrentValue = "partially installed" };
        var journal = new InstallJournal(id, SetupMode.Repair, InstallPhase.Switching, true,
            HistoricalSnapshot(populated));
        InstallTransaction.WriteAtomic(paths.Journal, JsonSerializer.SerializeToUtf8Bytes(journal));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            new InstallTransaction(paths, registration).Run(SetupMode.Repair, null, null, null,
                new CancellationToken(canceled: true)));

        Assert.Equal("old app", File.ReadAllText(paths.Executable));
        Assert.False(Directory.Exists(Backup(paths, id)));
        Assert.False(Directory.Exists(Stage(paths, id)));
        Assert.False(File.Exists(paths.Journal));
        Assert.Equal("restored", registration.CurrentValue);
        Assert.Equal(1, registration.RestoreCalls);
    }

    [Fact]
    public void LargeValidRegistrationSnapshotCanRestoreAnInterruptedSwap()
    {
        var paths = Paths();
        Guid id = PrepareInterruptedSwap(paths);
        var registration = new Registration { CurrentValue = "partially installed" };
        SavedValue[] values = HistoricalSnapshot(populated: true).Values.ToArray();
        for (int i = 0; i < 4; i++) values[i] = Saved(RegistryValueKind.Binary, new byte[800 * 1024]);
        var journal = new InstallJournal(id, SetupMode.Repair, InstallPhase.Switching, true,
            new RegistrationSnapshot(null, null, values));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal);
        Assert.True(bytes.Length > 4 * 1024 * 1024);
        InstallTransaction.WriteAtomic(paths.Journal, bytes);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new InstallTransaction(paths, registration).Run(SetupMode.Repair, null, null, null,
                new CancellationToken(canceled: true)));

        Assert.Equal("old app", File.ReadAllText(paths.Executable));
        Assert.False(Directory.Exists(Backup(paths, id)));
        Assert.False(Directory.Exists(Stage(paths, id)));
        Assert.False(File.Exists(paths.Journal));
        Assert.Equal("restored", registration.CurrentValue);
        Assert.Equal(1, registration.RestoreCalls);
    }

    private InstallPaths Paths() => new(Path.Combine(_root, "Programs"), Path.Combine(_root, "Data"),
        Path.Combine(_root, "StartMenu"));

    private static Guid PrepareInterruptedSwap(InstallPaths paths)
    {
        Guid id = Guid.NewGuid();
        Directory.CreateDirectory(paths.App);
        File.WriteAllText(paths.Executable, "new app");
        File.WriteAllBytes(Path.Combine(paths.App, InstallPayload.Marker), id.ToByteArray());
        Directory.CreateDirectory(Backup(paths, id));
        File.WriteAllText(Path.Combine(Backup(paths, id), "VESPER.exe"), "old app");
        return id;
    }

    private static RegistrationSnapshot? MalformedSnapshot(string failure)
    {
        if (failure == "null-snapshot") return null;
        if (failure == "null-array") return new RegistrationSnapshot(null, null, null!);

        SavedValue[] values = Enumerable.Range(0, SnapshotValueCount)
            .Select(_ => new SavedValue(RegistryValueKind.Unknown, null)).ToArray();
        byte[]? manifest = null;
        byte[]? shortcut = null;
        switch (failure)
        {
            case "wrong-count":
                values = values[..^1];
                break;
            case "too-many-values":
                values = [.. values, new SavedValue(RegistryValueKind.Unknown, null)];
                break;
            case "null-entry":
                values[0] = null!;
                break;
            case "invalid-string":
                values[0] = Saved(RegistryValueKind.String, 42);
                break;
            case "invalid-null-string":
                values[0] = new SavedValue(RegistryValueKind.String, null);
                break;
            case "invalid-dword":
                values[0] = Saved(RegistryValueKind.DWord, 2147483648L);
                break;
            case "invalid-qword":
                values[0] = Saved(RegistryValueKind.QWord, 1.5m);
                break;
            case "invalid-base64":
                values[0] = Saved(RegistryValueKind.Binary, "not base64");
                break;
            case "invalid-multistring":
                values[0] = Saved(RegistryValueKind.MultiString, new string?[] { "one", null });
                break;
            case "value-with-unknown-kind":
                values[0] = Saved(RegistryValueKind.Unknown, "unexpected");
                break;
            case "undefined-kind":
                values[0] = Saved((RegistryValueKind)999, "unexpected");
                break;
            case "oversized-registry-value":
                values[0] = Saved(RegistryValueKind.Binary, new byte[1024 * 1024 + 1]);
                break;
            case "oversized-registry-string":
                values[0] = Saved(RegistryValueKind.String, new string('a', 1024 * 1024 / 2));
                break;
            case "oversized-manifest":
                manifest = new byte[1024 * 1024 + 1];
                break;
            case "oversized-shortcut":
                shortcut = new byte[1024 * 1024 + 1];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure));
        }
        return new RegistrationSnapshot(manifest, shortcut, values);
    }

    private static RegistrationSnapshot HistoricalSnapshot(bool populated)
    {
        SavedValue[] values = Enumerable.Range(0, SnapshotValueCount)
            .Select(_ => new SavedValue(RegistryValueKind.Unknown, null)).ToArray();
        byte[]? manifest = null;
        byte[]? shortcut = null;
        if (populated)
        {
            for (int i = 0; i < 3; i++) values[i] = Saved(RegistryValueKind.String, "C:\\VESPER\\native-host.json");
            values[3] = Saved(RegistryValueKind.String, "VESPER");
            values[4] = Saved(RegistryValueKind.ExpandString, "%LOCALAPPDATA%\\VESPER");
            values[5] = Saved(RegistryValueKind.String, "DarkOrbitWeaver");
            values[6] = Saved(RegistryValueKind.String, "C:\\VESPER");
            values[7] = Saved(RegistryValueKind.Binary, new byte[] { 1, 2, 3 });
            values[8] = Saved(RegistryValueKind.MultiString, new[] { "one", "two" });
            values[9] = Saved(RegistryValueKind.None, new byte[] { 4, 5 });
            values[10] = Saved(RegistryValueKind.DWord, 0);
            values[11] = Saved(RegistryValueKind.QWord, 1L);
            manifest = "{}"u8.ToArray();
            shortcut = [1, 2, 3];
        }
        return new RegistrationSnapshot(manifest, shortcut, values);
    }

    private static SavedValue Saved(RegistryValueKind kind, object value)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value));
        return new SavedValue(kind, document.RootElement.Clone());
    }

    private static string Stage(InstallPaths paths, Guid id) => paths.App + $".stage-{id:N}";
    private static string Backup(InstallPaths paths, Guid id) => paths.App + $".rollback-{id:N}";

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Registration : IInstallRegistration
    {
        public string CurrentValue { get; set; } = "unchanged";
        public int RestoreCalls { get; private set; }
        public RegistrationSnapshot Capture() => HistoricalSnapshot(populated: false);
        public void Install(string version) { }
        public void Uninstall() { }
        public void Restore(RegistrationSnapshot snapshot)
        {
            _ = snapshot.Values.Length;
            RestoreCalls++;
            CurrentValue = "restored";
        }
    }
}
