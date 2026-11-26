using Microsoft.Win32;

namespace VESPER;

/// <summary>
/// Registers the per-user Chrome/Edge/Brave native-messaging helper. The installer
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
    public static NativeHostRegistrationStatus RegistrationStatus => ReadRegistrationStatus();

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
        get => RegistrationStatus == NativeHostRegistrationStatus.Registered;
    }

    private static NativeHostRegistrationStatus ReadRegistrationStatus()
    {
        try
        {
            string?[] registeredPaths = ReadRegisteredManifestPaths();
            bool usesCurrentManifest = registeredPaths.Any(path => PathsEqual(path, ManifestPath));
            string? manifestContents = usesCurrentManifest && File.Exists(ManifestPath)
                ? File.ReadAllText(ManifestPath)
                : null;
            return NativeHostRegistration.GetRegistrationStatus(
                registeredPaths, manifestContents, ManifestPath, HostExecutable, File.Exists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.Security.SecurityException)
        {
            return NativeHostRegistrationStatus.Unknown;
        }
    }

    private static string? ReadRegisteredHost()
    {
        try
        {
            string?[] registeredPaths = ReadRegisteredManifestPaths();
            if (!registeredPaths.Any(path => PathsEqual(path, ManifestPath)) || !File.Exists(ManifestPath)) return null;
            return NativeHostRegistration.TryReadManifestPath(File.ReadAllText(ManifestPath), out string? hostPath)
                ? hostPath
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string?[] ReadRegisteredManifestPaths()
    {
        string?[] registeredPaths = new string?[BrowserKeys.Length];
        for (int i = 0; i < BrowserKeys.Length; i++)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(BrowserKeys[i]);
            object? value = key?.GetValue(null);
            if (value is not null && value is not string)
                throw new InvalidOperationException("The browser helper registration has an unexpected value.");
            registeredPaths[i] = value as string;
        }
        return registeredPaths;
    }

    /// <summary>Registers the helper only when no other copy needs to be replaced.</summary>
    public static string? Register(bool replaceExisting = false)
    {
        if (!HostPresent) return "The browser helper hasn't been built yet. Build Vesper.NativeHost or use the packaged app.";
        NativeHostRegistrationStatus status = RegistrationStatus;
        if (status == NativeHostRegistrationStatus.Unknown)
            return "The browser helper registration could not be checked. No changes were made.";
        if (status == NativeHostRegistrationStatus.Registered) return null;
        if (status == NativeHostRegistrationStatus.RepairRequired)
            return "The browser helper needs repair. Open VESPER → Settings → Browser → Repair browser connection.";
        if (status == NativeHostRegistrationStatus.AnotherCopyRegistered && !replaceExisting)
            return "Another browser helper registration is in use. Open VESPER Settings, then choose Connect browser helper under Browser to replace it.";

        return WriteRegistration();
    }

    /// <summary>Repairs a broken registration that points to this VESPER manifest.</summary>
    public static string? Repair()
    {
        if (!HostPresent) return "The browser helper hasn't been built yet. Build Vesper.NativeHost or use the packaged app.";
        if (RegistrationStatus != NativeHostRegistrationStatus.RepairRequired)
            return "This browser helper does not have a repairable registration. No changes were made.";
        return WriteRegistration();
    }

    private static string? WriteRegistration()
    {
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

    /// <summary>Disconnects a portable copy before it is moved or removed.</summary>
    public static string? Unregister()
    {
        if (RegistrationStatus != NativeHostRegistrationStatus.Registered)
            return $"The browser helper is not fully registered with this copy across {NativeHostRegistration.BrowserList}. No changes were made.";
        try
        {
            foreach (string subKey in BrowserKeys)
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
                if (key is not null && string.Equals(key.GetValue(null) as string, ManifestPath, StringComparison.OrdinalIgnoreCase))
                    key.DeleteValue("", throwOnMissingValue: false);
            }
            File.Delete(ManifestPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ex.Message;
        }
    }

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
