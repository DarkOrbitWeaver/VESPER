using VESPER;

namespace Vesper.Windows.Tests;

public sealed class DownloadGridTests
{
    [Fact]
    public void RevealScrollsToAnExistingRow()
    {
        using var grid = new DownloadGrid { Size = new Size(640, 300) };
        grid.CreateControl();
        DownloadItem[] items = Enumerable.Range(0, 50)
            .Select(_ => new DownloadItem { Id = Guid.NewGuid(), Name = "download.bin" })
            .ToArray();
        grid.SetItems(items);

        Assert.True(grid.Reveal(items[30].Id));
        Assert.True(grid.AutoScrollPosition.Y < 0);
        Assert.False(grid.Reveal(Guid.NewGuid()));
    }
}
