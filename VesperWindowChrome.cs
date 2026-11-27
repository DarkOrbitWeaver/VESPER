using System.Runtime.InteropServices;

namespace VESPER;

// Keep the real Windows caption buttons; request its dark treatment when VESPER is dark.
internal static class VesperWindowChrome
{
    private const int ShowMinimizedWithoutActivation = 7;
    private const uint ShowWindowMessage = 0x0018;
    private const uint FlashTray = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern int ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern int IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int FlashWindowEx(ref FlashInfo info);

    internal static void ShowMinimized(Form window)
    {
        // WinForms uses an activating show command for minimized windows.
        ShowWindow(window.Handle, ShowMinimizedWithoutActivation);
        // A minimized show can skip WM_SHOWWINDOW. Keep WinForms visibility in sync.
        if (!window.Visible && IsWindowVisible(window.Handle) != 0)
            SendMessageW(window.Handle, ShowWindowMessage, new IntPtr(1), IntPtr.Zero);
    }

    internal static void FlashTaskbar(Form window)
    {
        if (!window.IsHandleCreated || GetForegroundWindow() == window.Handle) return;
        var flash = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = window.Handle, Flags = FlashTray, Count = 3
        };
        FlashWindowEx(ref flash);
    }

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
