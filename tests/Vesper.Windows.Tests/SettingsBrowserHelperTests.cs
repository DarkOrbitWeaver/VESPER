using VESPER;

namespace Vesper.Windows.Tests;

public sealed class SettingsBrowserHelperTests
{
    private const string ManifestPath = @"C:\Users\test\AppData\Local\VESPER\native-host\com.vesper.downloader.json";
    private const string CurrentHost = @"C:\Users\test\AppData\Local\Programs\VESPER\vesper-host.exe";

    [Theory]
    [InlineData(true, (int)NativeHostRegistrationStatus.Registered, true, false)]
    [InlineData(true, (int)NativeHostRegistrationStatus.Registered, false, true)]
    [InlineData(true, (int)NativeHostRegistrationStatus.NotRegistered, true, true)]
    [InlineData(true, (int)NativeHostRegistrationStatus.RepairRequired, true, true)]
    [InlineData(true, (int)NativeHostRegistrationStatus.AnotherCopyRegistered, true, true)]
    [InlineData(true, (int)NativeHostRegistrationStatus.Unknown, false, false)]
    [InlineData(false, (int)NativeHostRegistrationStatus.RepairRequired, false, false)]
    public void BrowserActionVisibilityMatchesConnectionState(
        bool hostPresent, int status, bool installed, bool expected) =>
        Assert.Equal(expected, SettingsForm.ShouldShowBrowserHelperAction(
            hostPresent, (NativeHostRegistrationStatus)status, installed));

    [Fact]
    public void MissingRegistrationCanConnect()
    {
        Assert.Equal(NativeHostRegistrationStatus.NotRegistered,
            GetStatus([null, null, null], null));
    }

    [Fact]
    public void ValidRegistrationRequiresAllThreeBrowsers()
    {
        string manifest = NativeHostRegistration.Manifest(CurrentHost);
        Assert.Equal(NativeHostRegistrationStatus.Registered,
            GetStatus([ManifestPath, ManifestPath, ManifestPath], manifest));
        Assert.Equal(NativeHostRegistrationStatus.RepairRequired,
            GetStatus([ManifestPath, null, null], manifest));
    }

    [Theory]
    [MemberData(nameof(InvalidManifests))]
    public void InvalidManifestCanBeRepaired(string? manifest) =>
        Assert.Equal(NativeHostRegistrationStatus.RepairRequired,
            GetStatus([ManifestPath, ManifestPath, ManifestPath], manifest));

    public static IEnumerable<object?[]> InvalidManifests()
    {
        string valid = NativeHostRegistration.Manifest(CurrentHost);
        yield return ["{"]; // Invalid JSON.
        yield return [valid.Replace("\"type\": \"stdio\"", "\"type\": \"other\"", StringComparison.Ordinal)];
        yield return [valid.Replace(
            $"chrome-extension://{NativeHostRegistration.ExtensionId}/", "chrome-extension://wrong/", StringComparison.Ordinal)];
        yield return [NativeHostRegistration.Manifest(@"C:\missing\vesper-host.exe")];
    }

    [Fact]
    public void RegistrationOwnedByAnotherCopyNeedsExplicitConnect()
    {
        const string otherHost = @"C:\OtherVESPER\vesper-host.exe";
        string manifest = NativeHostRegistration.Manifest(otherHost);
        NativeHostRegistrationStatus status = NativeHostRegistration.GetRegistrationStatus(
            [ManifestPath, ManifestPath, ManifestPath], manifest, ManifestPath, CurrentHost,
            path => string.Equals(path, otherHost, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(NativeHostRegistrationStatus.AnotherCopyRegistered, status);
    }

    [Fact]
    public void ForeignBrowserManifestIsNeverTreatedAsRepairable()
    {
        string manifest = NativeHostRegistration.Manifest(CurrentHost);
        Assert.Equal(NativeHostRegistrationStatus.AnotherCopyRegistered,
            GetStatus([ManifestPath, @"C:\OtherVESPER\host.json", ManifestPath], manifest));
    }

    private static NativeHostRegistrationStatus GetStatus(IReadOnlyList<string?> registeredPaths, string? manifest) =>
        NativeHostRegistration.GetRegistrationStatus(
            registeredPaths, manifest, ManifestPath, CurrentHost,
            path => string.Equals(path, CurrentHost, StringComparison.OrdinalIgnoreCase));
}
