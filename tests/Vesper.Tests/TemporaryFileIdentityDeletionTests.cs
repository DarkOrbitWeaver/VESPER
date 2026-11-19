using Vesper.Engine;

namespace Vesper.Tests;

public sealed class TemporaryFileIdentityDeletionTests
{
    [WindowsFact]
    public void Deletes_a_temporary_file_that_matches_its_saved_identity()
    {
        string folder = NewFolder();
        string path = Path.Combine(folder, "owned.convert.mp4");
        File.WriteAllText(path, "stage output");
        string identity = GetIdentity(path);

        try
        {
            Assert.True(PathSafety.TryDeleteTemporaryFile(path, identity, out string error), error);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [WindowsFact]
    public void Preserves_a_same_content_replacement_with_a_different_identity()
    {
        string folder = NewFolder();
        string path = Path.Combine(folder, "owned.convert.mp4");
        string original = Path.Combine(folder, "original.convert.mp4");
        byte[] content = "same stage bytes"u8.ToArray();
        File.WriteAllBytes(path, content);
        string identity = GetIdentity(path);
        File.Move(path, original);
        File.WriteAllBytes(path, content);

        try
        {
            Assert.False(PathSafety.TryDeleteTemporaryFile(path, identity, out string error));
            Assert.Contains("different file", error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(content, File.ReadAllBytes(path));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [WindowsFact]
    public void Rejects_a_missing_or_malformed_identity_without_deleting()
    {
        string folder = NewFolder();
        string path = Path.Combine(folder, "owned.convert.mp4");
        File.WriteAllText(path, "stage output");

        try
        {
            Assert.False(PathSafety.TryDeleteTemporaryFile(path, null, out _));
            Assert.False(PathSafety.TryDeleteTemporaryFile(path, "not-an-identity", out _));
            Assert.Equal("stage output", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "vesper-owned-temp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string GetIdentity(string path)
    {
        using var file = PathSafety.OpenTemporaryHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return PathSafety.TemporaryFileIdentity(file)!;
    }
}
