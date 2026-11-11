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
    private static string FilePath => Path.Combine(Directory_, "settings.json");
    private static string LegacyPath => Path.Combine(Directory_, "ui-preview-settings.json");

    /// <summary>Set when the previous settings file could not be read; shown to the user once at startup.</summary>
    public static string? LoadWarning { get; private set; }

    public static VesperPreferences Load()
    {
        string path = File.Exists(FilePath) ? FilePath : LegacyPath;
        if (!File.Exists(path)) return new VesperPreferences();
        try
        {
            return JsonSerializer.Deserialize<VesperPreferences>(File.ReadAllText(path), Options) ?? new VesperPreferences();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            try
            {
                string bad = path + ".bad";
                File.Move(path, bad, overwrite: true);
                LoadWarning = $"VESPER could not read its settings file ({ex.Message}). It was kept as {Path.GetFileName(bad)} and defaults are in use.";
            }
            catch (IOException) { LoadWarning = "VESPER could not read its settings file and is using defaults."; }
            return new VesperPreferences();
        }
    }

    /// <summary>Returns an error message, or null on success. A failed save never loses the previous file.</summary>
    public static string? Save(VesperPreferences preferences)
    {
        try
        {
            Directory.CreateDirectory(Directory_);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(preferences, Options));
            File.Move(temp, FilePath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
