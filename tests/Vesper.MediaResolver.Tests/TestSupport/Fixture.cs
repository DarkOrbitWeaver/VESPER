namespace Vesper.MediaResolver.Tests.TestSupport;

public static class Fixture
{
    public static string Load(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
