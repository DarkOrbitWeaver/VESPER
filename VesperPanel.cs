namespace VESPER;

// Used for the few surfaces that paint their own decoration or border.
// Ordinary layout panels do not need a separate backing bitmap.
public sealed class VesperPanel : Panel
{
    public VesperPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }
}
