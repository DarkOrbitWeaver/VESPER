using Vesper.Engine;
using VESPER;

namespace Vesper.Windows.Tests;

public sealed class DownloadActionsTests
{
    private static readonly string[] RunnableExtensions =
    [
        ".exe", ".msi", ".msp", ".mst", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs",
        ".vbe", ".js", ".jse", ".wsf", ".wsh", ".scr", ".com", ".lnk", ".hta", ".jar",
        ".reg", ".msix", ".appx", ".appinstaller", ".application", ".appref-ms", ".url", ".scf",
        ".cpl", ".chm", ".pif", ".xll", ".one", ".library-ms", ".search-ms", ".settingcontent-ms",
        ".iso", ".img", ".vhd", ".vhdx"
    ];

    [Fact]
    public void OpenConfirmationCoversEveryRunnableAndMountableExtension()
    {
        Assert.Equal(40, RunnableExtensions.Length);
        Assert.Equal(RunnableExtensions.Length, RunnableExtensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (string extension in RunnableExtensions)
        {
            string path = Path.Combine("C:\\Downloads", "fixture" + extension.ToUpperInvariant());

            Assert.True(DownloadActions.RequiresOpenConfirmation(path), extension);
        }
    }

    [Fact]
    public void OpenConfirmationDoesNotBlockOrdinaryFiles()
    {
        Assert.False(DownloadActions.RequiresOpenConfirmation(Path.Combine("C:\\Downloads", "notes.txt")));
    }

    [Fact]
    public void OpenWarningUsesOnlyTheSanitizedNameFromTheFinalPath()
    {
        const string untrustedName = "Invoice\u202Egpj.exe";
        string sanitizedName = FileNames.Sanitize(untrustedName);
        var snapshot = new JobSnapshot
        {
            FileName = "source-name.txt",
            SourceUrl = "https://files.example/secret-name.jpg?token=private",
            FinalPath = Path.Combine("C:\\Downloads", sanitizedName)
        };

        string warning = DownloadActions.OpenConfirmationText(snapshot.FinalPath!);

        Assert.Equal("Invoicegpj.exe", sanitizedName);
        Assert.EndsWith(".exe", Path.GetExtension(snapshot.FinalPath));
        Assert.Contains(sanitizedName, warning);
        Assert.DoesNotContain(snapshot.FileName, warning);
        Assert.DoesNotContain(snapshot.SourceUrl, warning);
        Assert.DoesNotContain("private", warning);
        Assert.DoesNotContain("C:\\Downloads", warning);
        Assert.DoesNotContain('\u202E', warning);
        Assert.Contains("can run code", warning);
        Assert.Contains("open a shortcut", warning);
        Assert.Contains("mount a disk image", warning);
        Assert.Contains("Only open it if you trust the source.", warning);
        Assert.DoesNotContain("program or script", warning);
        Assert.DoesNotContain("Run it?", warning);
    }

    [Fact]
    public void CancelConfirmationShowsSinglePartialAndRetainedTotal()
    {
        const long retainedBytes = 1_500_000;
        var items = new[] { new JobSnapshot { CommittedBytes = retainedBytes } };

        string text = DownloadActions.CancelConfirmationText(1, items);

        Assert.StartsWith("Cancel this download and delete its saved partial?", text);
        Assert.Contains($"Saved data to be deleted: {Formatting.Bytes(retainedBytes)} across 1 partial.", text);
    }

    [Fact]
    public void CancelConfirmationShowsBatchCountAndCombinedRetainedTotal()
    {
        const long firstBytes = 1_000_000;
        const long secondBytes = 2_500_000;
        var items = new[]
        {
            new JobSnapshot { CommittedBytes = firstBytes },
            new JobSnapshot { CommittedBytes = secondBytes }
        };

        string text = DownloadActions.CancelConfirmationText(3, items);

        Assert.StartsWith("Cancel 3 downloads and delete their saved partials?", text);
        Assert.Contains($"Saved data to be deleted: {Formatting.Bytes(firstBytes + secondBytes)} across 2 partials.", text);
    }
}
