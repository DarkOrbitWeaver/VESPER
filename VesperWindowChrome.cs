using System.Runtime.InteropServices;

namespace VESPER;

// Keep the real Windows caption buttons; request its dark treatment when VESPER is dark.
internal static class VesperWindowChrome
{
    public static void ApplyNativeColors(VesperPalette palette)
    {
        if (System.ComponentModel.LicenseManager.UsageMode != System.ComponentModel.LicenseUsageMode.Designtime)
            Application.SetColorMode(palette.IsDark && !palette.HighContrast ? SystemColorMode.Dark : SystemColorMode.Classic);
    }

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

    public static void Apply(Form window, VesperPalette palette)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10) || !window.IsHandleCreated) return;
        try
        {
            int enabled = palette.IsDark && !palette.HighContrast ? 1 : 0;
            DwmSetWindowAttribute(window.Handle, 20, ref enabled, sizeof(int));
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                int background = palette.Botanical ? ColorTranslator.ToWin32(palette.Back) : -1;
                int text = palette.Botanical ? ColorTranslator.ToWin32(palette.Text) : -1;
                DwmSetWindowAttribute(window.Handle, 35, ref background, sizeof(int)); // DWMWA_CAPTION_COLOR
                DwmSetWindowAttribute(window.Handle, 36, ref text, sizeof(int)); // DWMWA_TEXT_COLOR
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}

// Opt in to the WinForms theme before the native scrollbars are created.
internal sealed class VesperScrollPanel : Panel
{
    protected override CreateParams CreateParams
    {
        get
        {
            SetStyle(ControlStyles.ApplyThemingImplicitly, true);
            return base.CreateParams;
        }
    }
}
