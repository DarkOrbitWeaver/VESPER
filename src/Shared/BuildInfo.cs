using System.Reflection;

namespace Vesper.Shared;

/// <summary>Which build is this? "1.0.1 (2620e76)" from the assembly's informational version.</summary>
internal static class BuildInfo
{
    public static string Version(Assembly assembly)
    {
        string raw = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3) ?? "unknown";
        int plus = raw.IndexOf('+');
        if (plus < 0) return raw;
        string commit = raw[(plus + 1)..];
        if (commit.Length > 9) commit = commit[..9];
        return $"{raw[..plus]} ({commit})";
    }

    /// <summary>Just the dotted number, for comparisons with the extension's manifest version.</summary>
    public static string Number(Assembly assembly)
    {
        string label = Version(assembly);
        int space = label.IndexOf(' ');
        return space < 0 ? label : label[..space];
    }
}
