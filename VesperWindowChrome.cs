using System.Runtime.InteropServices;

namespace VESPER;

// Keep the real Windows caption buttons; request its dark treatment when VESPER is dark.
internal static class VesperWindowChrome
{
    public static void Brand(Form window)
    {
        try
        {
            var icon = new Icon(UiKit.Asset("VESPER-Illustrated.ico"));
            window.Icon = icon;
            window.Disposed += (_, _) => icon.Dispose();
        }
        catch (IOException) { }
        catch (ArgumentException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    public static void Apply(Form window, bool dark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10) || !window.IsHandleCreated) return;
        try
        {
            int enabled = dark && !SystemInformation.HighContrast ? 1 : 0;
            DwmSetWindowAttribute(window.Handle, 20, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}
