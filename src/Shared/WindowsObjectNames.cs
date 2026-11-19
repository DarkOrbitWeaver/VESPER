using System.Security.Cryptography;
using System.Text;

namespace Vesper.Shared;

internal static class WindowsObjectNames
{
    internal static string NameForCurrentUser(string purpose) => NameForIdentity(purpose, CurrentIdentity());

    internal static string LocalNameForCurrentUser(string purpose) => @"Local\" + NameForCurrentUser(purpose);

    internal static string NameForIdentity(string purpose, string identity)
    {
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Contains('\\'))
            throw new ArgumentException("An object name purpose is required.", nameof(purpose));
        if (string.IsNullOrWhiteSpace(identity))
            throw new ArgumentException("A user identity is required.", nameof(identity));

        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        return purpose + "-" + suffix;
    }

    internal static string LocalNameForIdentity(string purpose, string identity) => @"Local\" + NameForIdentity(purpose, identity);

    private static string CurrentIdentity()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? Environment.UserName;
        }

        return Environment.UserName;
    }
}
