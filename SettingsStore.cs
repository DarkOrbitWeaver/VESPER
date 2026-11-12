using System.Text.Json;
using System.Text.Json.Serialization;

namespace VESPER;

/// <summary>
/// Persists <see cref="VesperPreferences"/> as JSON in %LocalAppData%\VESPER. Writes are atomic (temp file + move),
/// and an unreadable file is renamed to settings.json.bad rather than silently discarded.
/// </summary>
internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private static string Directory_ => EngineHost.DataDirectory;

    /// <summary>Set when the previous settings file could not be read; shown to the user once at startup.</summary>
    public static string? LoadWarning { get; private set; }

    public static VesperPreferences Load()
    {
        return Load(Directory_);
    }

    internal static VesperPreferences Load(string directory)
    {
        string settingsPath = Path.Combine(directory, "settings.json");
        string legacyPath = Path.Combine(directory, "ui-preview-settings.json");
        string path = File.Exists(settingsPath) ? settingsPath : legacyPath;
        if (!File.Exists(path)) return new VesperPreferences();
        try
        {
            return JsonSerializer.Deserialize<VesperPreferences>(File.ReadAllText(path), Options) ?? new VesperPreferences();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            try
            {
                string bad = NextBackupPath(path);
                File.Move(path, bad);
                LoadWarning = $"VESPER could not read its settings file ({ex.Message}). It was kept as {Path.GetFileName(bad)} and defaults are in use.";
            }
            catch (IOException) { LoadWarning = "VESPER could not read its settings file and is using defaults."; }
            return new VesperPreferences();
        }
    }

    /// <summary>Returns an error message, or null on success. A failed save never loses the previous file.</summary>
    public static string? Save(VesperPreferences preferences)
        => Save(preferences, Directory_);

    internal static string? Save(VesperPreferences preferences, string directory)
    {
        string path = Path.Combine(directory, "settings.json");
        string temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, preferences, Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private static string NextBackupPath(string path)
    {
        string backup = path + ".bad";
        for (int suffix = 1; File.Exists(backup); suffix++)
            backup = path + ".bad." + suffix;
        return backup;
    }
}
