using Vesper.Engine;

namespace Vesper.Tests;

public sealed class WindowsDeviceNameTests
{
    [Theory]
    [InlineData("nul.tar.gz", "_nul.tar.gz")]
    [InlineData("aux.backup.zip", "_aux.backup.zip")]
    [InlineData("con.2026.csv", "_con.2026.csv")]
    [InlineData("COM¹.zip", "_COM¹.zip")]
    [InlineData("lpt³.tar", "_lpt³.tar")]
    [InlineData("Nul", "_Nul")]
    public void Device_name_is_checked_before_the_first_extension(string source, string expected) =>
        Assert.Equal(expected, FileNames.Sanitize(source));

    [Theory]
    [InlineData("nullable.txt")]
    [InlineData("console-notes.txt")]
    public void Ordinary_names_that_contain_a_device_name_are_preserved(string source) =>
        Assert.Equal(source, FileNames.Sanitize(source));

    [WindowsFact]
    public async Task Sanitized_windows_device_name_downloads_to_a_regular_file()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var harness = new Harness();

        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/file"), FileName: "nul.tar.gz"));
        Assert.True(added.Ok, added.Message);
        JobSnapshot done = await harness.WaitForStateAsync(added.JobId, JobState.Completed);

        Assert.Equal("_nul.tar.gz", Path.GetFileName(done.FinalPath));
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
    }
}
