using System.Security.Cryptography;
using System.Text.Json;

namespace Vesper.Setup;

internal enum SetupMode { Install, Repair, Uninstall }
internal enum InstallPhase { Staging, Switching, Committed }

internal sealed record InstallJournal(Guid Id, SetupMode Mode, InstallPhase Phase, bool HadApp,
    RegistrationSnapshot? Registration = null);

internal sealed class InstallTransaction(InstallPaths paths, IInstallRegistration registration, Func<IDisposable>? lockApp = null)
{
    internal void Run(SetupMode mode, InstallPayload? payload, string? setupExe, IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        CheckPath(paths.Lock);
        Directory.CreateDirectory(paths.Programs);
        using var setupLock = new FileStream(paths.Lock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            1, FileOptions.DeleteOnClose);
        RecoverPending();
        ct.ThrowIfCancellationRequested();
        CheckPath(paths.App);
        bool hadApp = Directory.Exists(paths.App);
        if (hadApp && !File.Exists(paths.Executable) && !File.Exists(Path.Combine(paths.App, InstallPayload.Marker)))
            throw new IOException("The installation folder contains files from another application.");
        // Catch a running instance before verifying the package. Recheck under the swap lock below.
        lockApp?.Invoke().Dispose();

        var journal = new InstallJournal(Guid.NewGuid(), mode, InstallPhase.Staging, hadApp);
        string stage = Stage(journal);
        string backup = Backup(journal);
        Save(journal);
        IDisposable? appLock = null;
        try
        {
            if (mode != SetupMode.Uninstall)
            {
                if (payload is null || setupExe is null) throw new InvalidDataException("The setup package is missing.");
                CheckPath(stage);
                Directory.CreateDirectory(stage);
                payload.Extract(stage, progress, ct);
                progress?.Report(new("Preparing repair and uninstall…"));
                CopySetup(setupExe, Path.Combine(stage, InstallPayload.SetupName), ct);
                WriteAtomic(Path.Combine(stage, InstallPayload.Marker), journal.Id.ToByteArray());
            }

            ct.ThrowIfCancellationRequested();
            appLock = lockApp?.Invoke();
            CheckPath(backup);
            journal = journal with { Phase = InstallPhase.Switching, Registration = registration.Capture() };
            Save(journal);
            progress?.Report(new(mode == SetupMode.Uninstall ? "Removing VESPER…" : "Registering VESPER and its browser helper…"));
            if (hadApp) Directory.Move(paths.App, backup);
            if (mode == SetupMode.Uninstall) registration.Uninstall();
            else
            {
                Directory.Move(stage, paths.App);
                registration.Install(payload!.Version);
            }
            ct.ThrowIfCancellationRequested();
            Save(journal with { Phase = InstallPhase.Committed });
        }
        catch (Exception failure)
        {
            try { Recover(); }
            catch (Exception recovery)
            {
                throw new IOException("Setup could not finish restoring the previous installation. Run setup again after closing VESPER.",
                    new AggregateException(failure, recovery));
            }
            throw;
        }
        finally { appLock?.Dispose(); }

        // A locked backup can be cleaned on the next setup run. The live install is already committed.
        try { Recover(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void RecoverPending()
    {
        if (!File.Exists(paths.Journal)) return;
        using var appLock = lockApp?.Invoke();
        Recover();
    }

    private void Recover()
    {
        CheckPath(paths.Journal);
        if (!File.Exists(paths.Journal)) return;
        using var stream = File.OpenRead(paths.Journal);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("The setup recovery record is invalid.");
        var journal = JsonSerializer.Deserialize<InstallJournal>(stream);
        stream.Dispose();
        if (journal is null || journal.Id == Guid.Empty || !Enum.IsDefined(journal.Phase) || !Enum.IsDefined(journal.Mode))
            throw new InvalidDataException("The setup recovery record is invalid.");
        string stage = Stage(journal);
        string backup = Backup(journal);
        CheckPath(paths.App);
        CheckPath(stage);
        CheckPath(backup);
        if (journal.Phase == InstallPhase.Switching)
        {
            if (journal.Registration is null) throw new InvalidDataException("The setup recovery record is incomplete.");
            if (IsNewInstallation(paths.App, journal.Id)) Directory.Move(paths.App, stage);
            if (Directory.Exists(backup)) Directory.Move(backup, paths.App);
            registration.Restore(journal.Registration);
        }
        else if (journal.Phase == InstallPhase.Committed) DeleteTree(backup);
        DeleteTree(stage);
        File.Delete(paths.Journal);
    }

    private string Stage(InstallJournal journal) => paths.App + $".stage-{journal.Id:N}";
    private string Backup(InstallJournal journal) => paths.App + $".rollback-{journal.Id:N}";
    private void Save(InstallJournal journal) => WriteAtomic(paths.Journal, JsonSerializer.SerializeToUtf8Bytes(journal));

    private static bool IsNewInstallation(string path, Guid id)
    {
        string marker = Path.Combine(path, InstallPayload.Marker);
        CheckPath(marker);
        var info = new FileInfo(marker);
        return info.Exists && info.Length == 16 && new Guid(File.ReadAllBytes(marker)) == id;
    }

    private static void CopySetup(string source, string destination, CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var to = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int count;
        while ((count = from.Read(buffer)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            to.Write(buffer, 0, count);
            hash.AppendData(buffer, 0, count);
        }
        to.Position = 0;
        if (!hash.GetHashAndReset().AsSpan().SequenceEqual(SHA256.HashData(to)))
            throw new InvalidDataException("The repair and uninstall program failed verification.");
    }

    internal static void CheckPath(string path)
    {
        for (string? part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Setup cannot use a link at {part}.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static void WriteAtomic(string path, byte[] content)
    {
        CheckPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    private static void DeleteTree(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) return;
        foreach (var child in directory.EnumerateFileSystemInfos())
        {
            if (child is DirectoryInfo sub && (sub.Attributes & FileAttributes.ReparsePoint) == 0)
                DeleteTree(sub.FullName);
            else child.Delete();
        }
        directory.Delete();
    }
}
