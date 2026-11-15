using System.Globalization;
using System.Text;

namespace Vesper.Shared;

/// <summary>
/// Character rules shared by the engine's and the resolver's file-name sanitizers.
/// VESPER saves to Windows volumes, so the Windows rules apply on every platform the code runs or is tested on.
/// </summary>
internal static class FileNameRules
{
    private const string WindowsInvalid = "<>:\"/\\|?*";

    /// <summary>Not allowed in a Windows file name. ':' is included because it would open an alternate data stream.</summary>
    public static bool IsWindowsInvalid(Rune rune) =>
        rune.IsBmp && (rune.Value < 32 || rune.Value == 127 || WindowsInvalid.Contains((char)rune.Value));

    /// <summary>
    /// Characters that take up no space or change the reading direction: control codes, zero-width marks,
    /// bidi overrides such as U+202E, line separators and language tags. A server can use them to make
    /// "Invoice[U+202E]gpj.exe" display as "Invoiceexe.jpg", so they never reach a saved name.
    /// </summary>
    public static bool IsHidden(Rune rune) => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
        or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
    public static bool IsReservedStem(string name)
    {
        string stem = name.Split('.')[0].Trim().ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") return true;
        return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
            && "123456789¹²³⁴⁵⁶⁷⁸⁹".Contains(stem[3]);
    }

}
