using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using VESPER;

namespace Vesper.Setup;

internal sealed record InstallPaths(string Programs, string Data, string StartMenu)
{
    internal string App => Path.Combine(Programs, "VESPER");
    internal string Executable => Path.Combine(App, "VESPER.exe");
    internal string Host => Path.Combine(App, "vesper-host.exe");
    internal string Setup => Path.Combine(App, InstallPayload.SetupName);
    internal string Journal => App + ".setup.json";
    internal string Lock => App + ".setup.lock";
    internal string Manifest => NativeHostRegistration.ManifestPath(Data);
    internal string Shortcut => Path.Combine(StartMenu, "VESPER.lnk");

    internal static InstallPaths ForUser()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string menu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (!Path.IsPathFullyQualified(local) || !Path.IsPathFullyQualified(menu))
            throw new IOException("Windows could not locate this account's installation folders.");
        return new(Path.Combine(local, "Programs"), Path.Combine(local, "VESPER"), menu);
    }
}

internal sealed record SavedValue(RegistryValueKind Kind, JsonElement? Value);
internal sealed record RegistrationSnapshot(byte[]? Manifest, byte[]? Shortcut, SavedValue[] Values);

internal interface IInstallRegistration
{
    RegistrationSnapshot Capture();
    void Install(string version);
    void Uninstall();
    void Restore(RegistrationSnapshot snapshot);
}

internal sealed class WindowsInstallRegistration(InstallPaths paths) : IInstallRegistration
{
    private const string AppsKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\VESPER";
    private static readonly string[] AppValues = ["DisplayName", "DisplayVersion", "Publisher", "InstallLocation",
        "DisplayIcon", "UninstallString", "ModifyPath", "NoModify", "NoRepair"];
    private static readonly (string Key, string Name)[] Values = [.. NativeHostRegistration.BrowserKeys.Select(k => (k, "")),
        .. AppValues.Select(n => (AppsKey, n))];

    public RegistrationSnapshot Capture() => new(ReadFile(paths.Manifest), ReadFile(paths.Shortcut),
        Values.Select(value => ReadValue(value.Key, value.Name)).ToArray());

    public void Install(string version)
    {
        InstallTransaction.WriteAtomic(paths.Manifest, Encoding.UTF8.GetBytes(NativeHostRegistration.Manifest(paths.Host)));
        CreateShortcut();
        foreach (string key in NativeHostRegistration.BrowserKeys) SetValue(key, "", paths.Manifest);

        string command = $"\"{paths.Setup}\"";
        object[] app = ["VESPER", version, "DarkOrbitWeaver", paths.App, paths.Executable, command + " --uninstall",
            command + " --repair", 0, 0];
        for (int i = 0; i < AppValues.Length; i++) SetValue(AppsKey, AppValues[i], app[i]);
    }

    public void Uninstall()
    {
        byte[]? manifest = ReadFile(paths.Manifest);
        if (manifest is not null && IsOurHost(manifest))
        {
            foreach (string key in NativeHostRegistration.BrowserKeys)
                if (ReadValue(key, "").Value is { ValueKind: JsonValueKind.String } saved
                    && string.Equals(saved.GetString(), paths.Manifest, StringComparison.OrdinalIgnoreCase))
                    DeleteValue(key, "");
            File.Delete(paths.Manifest);
        }
        if (ReadValue(AppsKey, "InstallLocation").Value is { ValueKind: JsonValueKind.String } location
            && string.Equals(location.GetString(), paths.App, StringComparison.OrdinalIgnoreCase))
            foreach (string name in AppValues) DeleteValue(AppsKey, name);
        InstallTransaction.CheckPath(paths.Shortcut);
        File.Delete(paths.Shortcut);
    }

    public void Restore(RegistrationSnapshot snapshot)
    {
        if (snapshot.Values.Length != Values.Length) throw new InvalidDataException("The setup recovery record is incomplete.");
        for (int i = 0; i < Values.Length; i++)
        {
            var (key, name) = Values[i];
            var saved = snapshot.Values[i];
            if (saved.Value is not JsonElement value)
            {
                DeleteValue(key, name);
                continue;
            }
            object? decoded = saved.Kind switch
            {
                RegistryValueKind.String or RegistryValueKind.ExpandString => value.GetString(),
                RegistryValueKind.DWord => value.GetInt32(),
                RegistryValueKind.QWord => value.GetInt64(),
                RegistryValueKind.Binary or RegistryValueKind.None => value.GetBytesFromBase64(),
                RegistryValueKind.MultiString => value.Deserialize<string[]>(),
                _ => null
            };
            if (decoded is null) throw new InvalidDataException("Setup could not restore a saved registry value.");
            SetValue(key, name, decoded, saved.Kind);
        }
        RestoreFile(paths.Manifest, snapshot.Manifest);
        RestoreFile(paths.Shortcut, snapshot.Shortcut);
    }

    private bool IsOurHost(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.TryGetProperty("path", out var host) && host.ValueKind == JsonValueKind.String
                && string.Equals(host.GetString(), paths.Host, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    private void CreateShortcut()
    {
        InstallTransaction.CheckPath(paths.Shortcut);
        Directory.CreateDirectory(paths.StartMenu);
        string temp = Path.Combine(paths.StartMenu, $"VESPER-{Guid.NewGuid():N}.lnk");
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(temp);
            shortcut.TargetPath = paths.Executable;
            shortcut.WorkingDirectory = paths.App;
            shortcut.IconLocation = paths.Executable + ",0";
            shortcut.Save();
            File.Move(temp, paths.Shortcut, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    private static byte[]? ReadFile(string path)
    {
        InstallTransaction.CheckPath(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("A setup registration file is too large to back up.");
        return File.ReadAllBytes(path);
    }

    private static void RestoreFile(string path, byte[]? content)
    {
        if (content is not null) InstallTransaction.WriteAtomic(path, content);
        else
        {
            InstallTransaction.CheckPath(path);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static SavedValue ReadValue(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        object? value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null ? new(RegistryValueKind.Unknown, null)
            : new(key!.GetValueKind(name), JsonSerializer.SerializeToElement(value, value.GetType()));
    }

    private static void SetValue(string path, string name, object value, RegistryValueKind kind = RegistryValueKind.Unknown)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path);
        key.SetValue(name, value, kind);
    }

    private static void DeleteValue(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
