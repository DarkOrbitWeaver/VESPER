using Vesper.Shared;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class WindowsObjectNamesTests
{
    [Fact]
    public void Bridge_pipe_uses_the_shared_identity_name()
    {
        const string identity = "S-1-5-21-1234-5678-9012-1001";

        Assert.Equal(WindowsObjectNames.NameForIdentity("vesper", identity),
            BridgePipe.NameForIdentity("vesper", identity));
    }

    [Fact]
    public void Names_differ_by_identity_and_purpose()
    {
        string first = WindowsObjectNames.NameForIdentity("VESPER.SingleInstance", "S-1-user-a");
        string second = WindowsObjectNames.NameForIdentity("VESPER.SingleInstance", "S-1-user-b");
        string show = WindowsObjectNames.NameForIdentity("VESPER.Show", "S-1-user-a");

        Assert.NotEqual(first, second);
        Assert.NotEqual(first, show);
        Assert.StartsWith("VESPER.SingleInstance-", first);
    }
}
