using System.Text.Json;

namespace VESPER;

// Only visual preferences are saved. Transfer records are always temporary samples.
internal static class PreviewPreferences
{
    private static string FileName => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VESPER", "ui-preview-settings.json");

    public static VesperPreferences Load()
    {
        try
        {
            if (File.Exists(FileName))
                return JsonSerializer.Deserialize<VesperPreferences>(File.ReadAllText(FileName)) ?? new VesperPreferences();
        }
        catch (Exception) { /* Corrupt/old preview preferences are safe to ignore. */ }
        return new VesperPreferences();
    }

    public static void Save(VesperPreferences preferences)
    {
        try
        {
            string path = FileName;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(preferences));
        }
        catch (Exception) { /* Nonessential to UI preview; the session still retains changes. */ }
    }
}
