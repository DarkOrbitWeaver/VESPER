using System.Globalization;
using System.Text;
using Vesper.Engine;

namespace Vesper.Tests;

public class ExtentSetParseTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(", ,")]
    public void Empty_input_parses_as_an_empty_set(string? text)
    {
        Assert.True(ExtentSet.TryParse(text, out ExtentSet set));
        Assert.True(set.IsEmpty);
        Assert.Equal(0, set.TotalBytes);
    }

    [Fact]
    public void Unsorted_overlapping_and_adjacent_ranges_are_normalized()
    {
        Assert.True(ExtentSet.TryParse("8-10, 0-5, 4-8, 20-21, 21-25, 18-20", out ExtentSet set));

        Assert.Equal("0-10,18-25", set.Serialize());
        Assert.Equal(17, set.TotalBytes);
    }

    [Fact]
    public void Invalid_input_leaves_an_empty_set()
    {
        Assert.False(ExtentSet.TryParse("0-10,20-30,31-31", out ExtentSet set));

        Assert.True(set.IsEmpty);
        Assert.Equal(0, set.TotalBytes);
    }

    [Fact]
    public void Maximum_extent_length_is_preserved_without_overflow()
    {
        string text = $"0-{long.MaxValue.ToString(CultureInfo.InvariantCulture)}";

        Assert.True(ExtentSet.TryParse(text, out ExtentSet set));

        Assert.Equal(long.MaxValue, set.TotalBytes);
        Assert.Equal(text, set.Serialize());
    }

    [Fact]
    public void Large_fragment_list_parses_and_normalizes_with_bounded_work()
    {
        const int fragmentCount = 20_000;
        var text = new StringBuilder(fragmentCount * 16);
        for (int i = fragmentCount - 1; i >= 0; i--)
        {
            if (text.Length > 0) text.Append(',');
            text.Append(i * 4).Append('-').Append(i * 4 + 2);
        }

        Assert.True(ExtentSet.TryParse(text.ToString(), out ExtentSet set));

        Assert.Equal(fragmentCount, set.Ranges.Count);
        Assert.Equal(fragmentCount * 2L, set.TotalBytes);
        Assert.Equal(new ByteRange(0, 2), set.Ranges[0]);
        Assert.Equal(new ByteRange((fragmentCount - 1) * 4L, (fragmentCount - 1) * 4L + 2), set.Ranges[^1]);
    }
}
