using System.Text.Json;
using Microsoft.Win32;

namespace VESPER;

/// <summary>
/// Registers the per-user Chrome/Edge native-messaging helper. The installer
/// puts it beside VESPER.exe; Visual Studio builds keep it in its own project.
/// </summary>
internal static class NativeHostSetup
{
    public const string HostName = NativeHostRegistration.HostName;
    public const string ExtensionId = NativeHostRegistration.ExtensionId;
    private static string[] BrowserKeys => NativeHostRegistration.BrowserKeys;

    private static string? ProjectFolder
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            for (int i = 0; directory is not null && i < 8; i++, directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "VESPER.csproj"))) return directory.FullName;
            return null;
        }
    }

    public static string HostExecutable
    {
        get
        {
            string installed = Path.Combine(AppContext.BaseDirectory, "vesper-host.exe");
            if (File.Exists(installed)) return installed;
            string? root = ProjectFolder;
            if (root is not null)
            {
                // Known output layouts only: never run an executable found by an arbitrary recursive scan.
                foreach (string configuration in new[] { "Debug", "Release" })
                foreach (string target in new[] { "net10.0", Path.Combine("net10.0", "win-x64") })
                {
                    string candidate = Path.Combine(root, "src", "Vesper.NativeHost", "bin", configuration, target, "vesper-host.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return installed;
        }
    }

    public static string ExtensionFolder
    {
        get
        {
            string installed = Path.Combine(AppContext.BaseDirectory, "extension");
            if (File.Exists(Path.Combine(installed, "manifest.json"))) return installed;
            return Path.Combine(ProjectFolder ?? AppContext.BaseDirectory, "extension");
        }
    }

    public static bool IsInstalledApp => Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))
        .Equals(Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "VESPER")), StringComparison.OrdinalIgnoreCase);

    public static string ManifestPath => NativeHostRegistration.ManifestPath(EngineHost.DataDirectory);
    public static bool HostPresent => File.Exists(HostExecutable);
    public static bool ExtensionPresent => File.Exists(Path.Combine(ExtensionFolder, "manifest.json"));

    // Refresh a registration owned by this checkout without replacing an installed copy.
    public static bool HasDevelopmentRegistration
    {
        get
        {
            string? root = ProjectFolder;
            if (root is null || ReadRegisteredHost() is not { } path) return false;
            string relative = Path.GetRelativePath(root, path);
            return relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith(Path.Combine("src", "Vesper.NativeHost", "bin") + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
    }

    public static bool IsRegistered
    {
        get
        {
            return ReadRegisteredHost() is { } path && File.Exists(path)
                && string.Equals(path, Path.GetFullPath(HostExecutable), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? ReadRegisteredHost()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(BrowserKeys[0]);
            if (key?.GetValue(null) as string != ManifestPath || !File.Exists(ManifestPath)) return null;
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(ManifestPath));
            string? path = manifest.RootElement.GetProperty("path").GetString();
            return Path.IsPathFullyQualified(path ?? "") ? Path.GetFullPath(path!) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
            or InvalidOperationException or ArgumentException or System.Security.SecurityException) { return null; }
    }

    /// <summary>Returns an error message, or null when the helper was registered.</summary>
    public static string? Register()
    {
        if (!HostPresent) return "The browser helper hasn't been built yet. Build Vesper.NativeHost or use the packaged app.";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
            File.WriteAllText(ManifestPath, NativeHostRegistration.Manifest(HostExecutable));
            foreach (string subKey in BrowserKeys)
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(subKey);
                key.SetValue(null, ManifestPath);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ex.Message;
        }
    }

    /// <summary>Remove only browser registration; never touch downloads, settings or history.</summary>
    public static string? Unregister()
    {
        try
        {
            foreach (string subKey in BrowserKeys) Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            if (File.Exists(ManifestPath)) File.Delete(ManifestPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ex.Message;
        }
    }
}
