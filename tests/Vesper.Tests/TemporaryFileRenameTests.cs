using Vesper.Engine;

namespace Vesper.Tests;

public sealed class TemporaryFileRenameTests
{
    [WindowsFact]
    public void Moves_a_temporary_file_without_replacing_another_file()
    {
        string folder = NewFolder();
        string source = Path.Combine(folder, "source.convert.tmp");
        string target = Path.Combine(folder, "source.convert.mp4");
        byte[] content = "verified conversion output"u8.ToArray();
        File.WriteAllBytes(source, content);
        string identity = GetIdentity(source);

        try
        {
            Assert.True(PathSafety.TryMoveTemporaryFile(source, target, identity, out string error), error);
            Assert.False(File.Exists(source));
            Assert.Equal(content, File.ReadAllBytes(target));
            Assert.Equal(identity, GetIdentity(target));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [WindowsFact]
    public void Reuses_a_recently_vacated_name_without_losing_file_identity()
    {
        string folder = NewFolder();
        string source = Path.Combine(folder, "merge-output.tmp");
        string target = Path.Combine(folder, "download.partial");
        string backup = Path.Combine(folder, "download.tracks");
        try
        {
            File.WriteAllText(target, "original tracks");
            File.SetCreationTimeUtc(target, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.WriteAllText(source, "merged output");
            File.SetCreationTimeUtc(source, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            string identity = GetIdentity(source);
            Assert.True(PathSafety.TryMoveTemporaryFile(target, backup, GetIdentity(target), out string error), error);
            Assert.True(PathSafety.TryMoveTemporaryFile(source, target, identity, out error), error);
            Assert.Equal(identity, GetIdentity(target));
            Assert.Equal("merged output", File.ReadAllText(target));
            Assert.Equal("original tracks", File.ReadAllText(backup));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [WindowsFact]
    public void Restores_only_the_timestamp_of_the_same_file_id()
    {
        string folder = NewFolder();
        string source = Path.Combine(folder, "source.tmp");
        string other = Path.Combine(folder, "other.tmp");
        try
        {
            File.WriteAllText(source, "output");
            File.WriteAllText(other, "output");
            string identity = GetIdentity(source);
            File.SetCreationTimeUtc(source, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            using (var file = PathSafety.OpenTemporaryHandle(source, FileMode.Open, FileAccess.Read, FileShare.Read, writeAttributes: true))
            {
                Assert.NotEqual(identity, PathSafety.TemporaryFileIdentity(file));
                PathSafety.RestoreMovedTemporaryIdentity(file, identity);
                Assert.Equal(identity, PathSafety.TemporaryFileIdentity(file));
            }
            using var different = PathSafety.OpenTemporaryHandle(other, FileMode.Open, FileAccess.Read, FileShare.Read, writeAttributes: true);
            Assert.Throws<IOException>(() => PathSafety.RestoreMovedTemporaryIdentity(different, identity));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [WindowsFact]
    public void Pins_ancestor_directories_until_the_handle_bound_rename_finishes()
    {
        string folder = NewFolder();
        string leaf = Path.Combine(folder, "stages");
        Directory.CreateDirectory(leaf);
        string movedFolder = folder + "-moved";
        string source = Path.Combine(leaf, "source.convert.tmp");
        string target = Path.Combine(leaf, "source.convert.mp4");
        byte[] content = "verified conversion output"u8.ToArray();
        File.WriteAllBytes(source, content);
        string identity = GetIdentity(source);

        try
        {
            bool ancestorMoveWasBlocked = false;
            Assert.True(PathSafety.TryMoveTemporaryFile(source, target, identity, out string error,
                () =>
                {
                    try { Directory.Move(folder, movedFolder); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        ancestorMoveWasBlocked = true;
                    }
                }), error);

            Assert.True(ancestorMoveWasBlocked);
            Assert.True(Directory.Exists(folder));
            Assert.False(Directory.Exists(movedFolder));
            Assert.Equal(content, File.ReadAllBytes(target));
            Assert.Equal(identity, GetIdentity(target));
        }
        finally
        {
            string cleanupFolder = Directory.Exists(movedFolder) ? movedFolder : folder;
            if (Directory.Exists(cleanupFolder)) Directory.Delete(cleanupFolder, recursive: true);
        }
    }

    [WindowsFact]
    public void Preserves_a_same_content_source_replacement()
    {
        string folder = NewFolder();
        string source = Path.Combine(folder, "source.convert.tmp");
        string oldSource = Path.Combine(folder, "source.original.tmp");
        string target = Path.Combine(folder, "source.convert.mp4");
        byte[] content = "same source bytes"u8.ToArray();
        File.WriteAllBytes(source, content);
        string identity = GetIdentity(source);
        File.Move(source, oldSource);
        File.WriteAllBytes(source, content);

        try
        {
            Assert.False(PathSafety.TryMoveTemporaryFile(source, target, identity, out _));
            Assert.Equal(content, File.ReadAllBytes(source));
            Assert.False(File.Exists(target));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [WindowsFact]
    public void Preserves_source_and_target_on_target_collision()
    {
        string folder = NewFolder();
        string source = Path.Combine(folder, "source.convert.tmp");
        string target = Path.Combine(folder, "source.convert.mp4");
        byte[] sourceContent = "source output"u8.ToArray();
        byte[] targetContent = "user file"u8.ToArray();
        File.WriteAllBytes(source, sourceContent);
        File.WriteAllBytes(target, targetContent);
        string identity = GetIdentity(source);

        try
        {
            Assert.False(PathSafety.TryMoveTemporaryFile(source, target, identity, out _));
            Assert.Equal(sourceContent, File.ReadAllBytes(source));
            Assert.Equal(targetContent, File.ReadAllBytes(target));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [WindowsFact]
    public void Refuses_paths_in_different_directories()
    {
        string sourceFolder = NewFolder();
        string targetFolder = NewFolder();
        string source = Path.Combine(sourceFolder, "source.convert.tmp");
        string target = Path.Combine(targetFolder, "source.convert.mp4");
        File.WriteAllText(source, "source output");
        string identity = GetIdentity(source);

        try
        {
            Assert.False(PathSafety.TryMoveTemporaryFile(source, target, identity, out _));
            Assert.True(File.Exists(source));
            Assert.False(File.Exists(target));
        }
        finally
        {
            if (Directory.Exists(sourceFolder)) Directory.Delete(sourceFolder, recursive: true);
            if (Directory.Exists(targetFolder)) Directory.Delete(targetFolder, recursive: true);
        }
    }

    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "vesper-temp-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string GetIdentity(string path)
    {
        using var file = PathSafety.OpenTemporaryHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return PathSafety.TemporaryFileIdentity(file)!;
    }
}
