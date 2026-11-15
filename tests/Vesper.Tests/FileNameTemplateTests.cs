using Vesper.Engine;

namespace Vesper.Tests;

public sealed class FileNameTemplateTests
{
    [Fact]
    public void Template_adds_source_site_and_keeps_the_extension_at_the_end()
    {
        string name = FileNames.ApplyTemplate("Coastal walk.mp4", "{name} - {host}", new Uri("https://media.example/video"));

        Assert.Equal("Coastal walk - media.example.mp4", name);
    }

    [Fact]
    public void Empty_template_keeps_the_existing_name()
    {
        string name = FileNames.ApplyTemplate("Coastal walk.mp4", "", new Uri("https://media.example/video"));

        Assert.Equal("Coastal walk.mp4", name);
    }

    [Theory]
    [InlineData("{host}")]
    [InlineData("{name} {unknown}")]
    [InlineData("{name")]
    [InlineData("{name}}")]
    public void Invalid_template_is_rejected_and_ignored(string template)
    {
        Assert.False(FileNames.TryValidateTemplate(template, out _));
        Assert.Equal("clip.mkv", FileNames.ApplyTemplate("clip.mkv", template, new Uri("https://media.example/video")));
    }

    [Fact]
    public async Task Http_content_disposition_name_uses_the_template()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var harness = new Harness(tweak: options => options.FileNameTemplate = "{name} - {host}");

        JobSnapshot done = await harness.WaitForStateAsync(harness.Add(server.Url("/disposition")), JobState.Completed);

        Assert.Equal("evil - 127.0.0.1.txt", Path.GetFileName(done.FinalPath));
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
    }

    [Fact]
    public async Task Missing_content_disposition_does_not_apply_the_template_twice()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var harness = new Harness(tweak: options => options.FileNameTemplate = "{name} - {host}");

        JobSnapshot done = await harness.WaitForStateAsync(harness.Add(server.Url("/file")), JobState.Completed);

        Assert.Equal("file - 127.0.0.1", Path.GetFileName(done.FinalPath));
    }
}
