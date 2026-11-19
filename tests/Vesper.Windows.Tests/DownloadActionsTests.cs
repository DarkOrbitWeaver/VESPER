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
    }
}
