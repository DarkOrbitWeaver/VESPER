using Vesper.Shared;

namespace Vesper.Setup.Tests;

public sealed class WindowsObjectNamesTests
{
    [Fact]
    public void Setup_mutex_uses_the_shared_current_user_name()
    {
        Assert.Equal(WindowsObjectNames.LocalNameForCurrentUser("VESPER.SingleInstance"), Program.InstanceMutexName);
    }

    [Fact]
    public void Local_kernel_object_names_differ_by_identity()
    {
        string first = WindowsObjectNames.LocalNameForIdentity("VESPER.SingleInstance", "S-1-user-a");
        string second = WindowsObjectNames.LocalNameForIdentity("VESPER.SingleInstance", "S-1-user-b");

        Assert.StartsWith(@"Local\VESPER.SingleInstance-", first);
        Assert.NotEqual(first, second);
    }
}
