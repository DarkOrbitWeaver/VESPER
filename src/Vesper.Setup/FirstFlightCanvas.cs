using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using VESPER;

namespace Vesper.Setup;

internal sealed class FirstFlightCanvas : Control
{
    internal const int LogicalWidth = 800;
    internal const int LogicalHeight = 484;
    internal const double PublisherStart = 3.06;
    internal const double BlankStart = 5.03;
    internal const double Duration = 5.25;
    private const double FlightStart = 1.18;
    private const double ForegroundPass = 2.95;
    private const double HoldStart = 4.23;
    private const double FadeStart = 4.75;
    private static readonly double[] Cuts =
        [0, .12, .26, .43, .57, .70, .88, 1.04, FlightStart, ForegroundPass, PublisherStart, HoldStart, FadeStart, BlankStart];
    private static readonly string[] Props = ["01_hourglass", "02_key", "03_letter", "04_quill", "05_camera", "06_vinyl", "07_book", "08_moon"];

    // Draw the trailing butterflies first so the closest wings can pass in front of them.
    private static readonly Flight[] Flights =
    [
        new(-44, 86, 17, 3.04f, -.18f),
        new(53, -83, 20, 2.99f, .20f),
        new(129, 69, 25, 2.93f, .14f),
        new(-113, -74, 27, 2.88f, -.24f),
        new(160, -24, 19, 2.83f, .24f),
        new(-155, 46, 23, 2.78f, -.15f),
        new(25, 120, 29, 2.73f, .12f),
        new(-8, -113, 26, 2.68f, -.10f),
        new(-127, -10, 31, 2.63f, -.32f),
        new(93, -66, 34, 2.59f, .22f),
        new(-76, 55, 37, 2.55f, -.12f),
        new(85, 69, 48, 2.50f, .29f),
        new(90, 16, 40, 2.40f, .17f),
        new(55, -40, 58, 2.29f, .16f),
        new(-16, 8, 88, 2.19f, -.30f)
    ];

    private readonly Font _heading = new("Georgia", 56, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _subtitle = new("Georgia", 11, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Dictionary<string, Bitmap> _art = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private VesperPalette _palette;
    private double _clockOffset;
    private double _lastFrame;
    private bool _holdBeforeReveal;
    private bool _showWelcomeArt = true;
    private string _holdMessage = "";

    internal void SetHoldMessage(string message) => _holdMessage = message;

    internal event Action? IntroFinished;
    internal bool Playing => _timer.Enabled;

    internal FirstFlightCanvas(VesperPalette palette)
    {
        _palette = palette;
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        TabStop = false;
        _timer.Tick += (_, _) =>
        {
            if (_lastFrame >= Duration)
            {
                StopIntro();
                IntroFinished?.Invoke();
                return;
            }
            _timer.Interval = _holdBeforeReveal && _lastFrame >= FadeStart ? 100 : 16;
            Invalidate();
        };
    }

    internal void SetPalette(VesperPalette palette)
    {
        _palette = palette;
        ClearArt();
        Invalidate();
    }

    internal bool TryStartIntro(bool holdForSetup = false)
    {
        ClearArt();
        _showWelcomeArt = true;
        try
        {
            PreloadIntroArt();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or ExternalException or OutOfMemoryException)
        {
            ClearArt();
            _showWelcomeArt = false;
            Invalidate();
            return false;
        }

        _clockOffset = 0;
        _lastFrame = -1;
        _holdBeforeReveal = holdForSetup;
        _holdMessage = "";
        // Start timing on the first paint.
        _clock.Reset();
        _timer.Interval = 16;
        _timer.Start();
        Invalidate();
        return true;
    }

    internal void ReleaseHold()
    {
        if (!_holdBeforeReveal) return;
        IntroTime();
        _holdBeforeReveal = false;
        Invalidate();
    }

    internal void StopIntro()
    {
        if (!Playing) return;
        _holdBeforeReveal = false;
        _timer.Stop();
        _clock.Stop();
        ClearArt();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        double? seconds = null;
        if (Playing)
        {
            if (!_clock.IsRunning) _clock.Start();
            double time = IntroTime();
            _lastFrame = NextFrame(_lastFrame, time);
            _clockOffset += time - _lastFrame;
            seconds = _lastFrame;
        }
        Render(e.Graphics, ClientSize, seconds);
    }

    private double IntroTime()
    {
        double time = _clock.Elapsed.TotalSeconds - _clockOffset;
        if (_holdBeforeReveal && time > FadeStart)
        {
            _clockOffset += time - FadeStart;
            time = FadeStart;
        }
        return time;
    }

    // After a stall, play the next scene that would have been skipped.
    internal static double NextFrame(double previous, double elapsed)
    {
        int next = Array.FindIndex(Cuts, cut => cut > previous);
        return next >= 0 && next + 1 < Cuts.Length && elapsed >= Cuts[next + 1]
            ? Cuts[next]
            : elapsed;
    }

    private void PreloadIntroArt()
    {
        foreach (string prop in Props) Art("objects." + prop);
        Art("atlas");
        Emblem();
        Art("brand.wordmark_" + (_palette.IsDark ? "dark" : "light"));
    }

    internal void Render(Graphics graphics, Size size, double? seconds)
    {
        var saved = graphics.Save();
        try
        {
            graphics.Clear(_palette.Back);
            graphics.ScaleTransform(size.Width / (float)LogicalWidth, size.Height / (float)LogicalHeight);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            if (seconds is double time) DrawIntro(graphics, time);
            else if (_showWelcomeArt) DrawWelcome(graphics);
        }
        finally { graphics.Restore(saved); }
    }

    private void DrawWelcome(Graphics graphics)
    {
        if (!_palette.HighContrast)
        {
            Center(graphics, Art("botanical.blossom_header"), 660, 45, 315, 140, .78f);
            Center(graphics, Art("botanical.hills_right", feather: true), 680, 457, 415, 94, _palette.IsDark ? .24f : .28f);
            Center(graphics, Art("botanical.hills_left", feather: true), 112, 473, 275, 78, _palette.IsDark ? .14f : .17f);
            using var orbit = new Pen(Color.FromArgb(90, _palette.MutedGreen));
            graphics.DrawEllipse(orbit, 338, 35, 124, 124);
            graphics.DrawEllipse(orbit, 300, 84, 200, 24);
            Center(graphics, Art("butterfly.VESPER_original_front"), 400, 108, 190, 132);
            graphics.DrawLine(orbit, 309, 281, 373, 281);
            graphics.DrawLine(orbit, 427, 281, 491, 281);
            VesperDrawing.Icon(graphics, VesperIcon.Leaf, new Rectangle(391, 272, 18, 18), _palette.MutedGreen);
        }
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var ink = new SolidBrush(_palette.Text);
        graphics.DrawString("V E S P E R", _heading, ink, new RectangleF(180, 185, 440, 67), format);
        graphics.DrawString("D O W N L O A D     M A N A G E R", _subtitle, ink, new RectangleF(180, 246, 440, 25), format);
    }

    private void DrawIntro(Graphics graphics, double time)
    {
        if (time < FlightStart)
        {
            int index = Math.Max(0, Array.FindLastIndex(Cuts, cut => cut <= time));
            Center(graphics, Art("objects." + Props[index]), 400, 236, 224, 220);
            return;
        }
        if (time >= BlankStart) return;
        if (time > PublisherStart)
        {
            float fade = 1 - Math.Clamp((float)((time - FadeStart) / (BlankStart - FadeStart)), 0, 1);
            float reveal = Math.Min((float)((time - PublisherStart) / .42), 1);
            float opacity = reveal * reveal * (3 - 2 * reveal) * fade;
            Bitmap emblem = Emblem();
            Draw(graphics, emblem, new RectangleF(235, 109, emblem.Width, emblem.Height), opacity);
            string theme = _palette.IsDark ? "dark" : "light";
            Bitmap wordmark = Art("brand.wordmark_" + theme);
            float height = 376f * wordmark.Height / wordmark.Width;
            Draw(graphics, wordmark, new RectangleF(212, 323, 376, height), opacity);
        }
        if (time < HoldStart)
            DrawFlight(graphics, Art("atlas"), (float)(time - FlightStart));
        if (_holdBeforeReveal && _holdMessage.Length > 0 && time >= HoldStart)
        {
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var ink = new SolidBrush(_palette.Muted);
            graphics.DrawString(_holdMessage, _subtitle, ink, new RectangleF(160, 396, 480, 28), format);
        }
    }

    private Bitmap Emblem()
    {
        if (_art.TryGetValue("emblem", out var cached)) return cached;
        var emblem = new Bitmap(330, 170, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(emblem);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var disk = new GraphicsPath();
            disk.AddEllipse(97, 17, 136, 136);
            using var shade = new PathGradientBrush(disk)
            {
                CenterPoint = new PointF(147, 59),
                CenterColor = Color.FromArgb(55, 75, 53),
                SurroundColors = [Color.FromArgb(12, 25, 17)]
            };
            graphics.FillPath(shade, disk);

            // Derive the whole ring and front rim from one source so the arcs meet without seams.
            using var originalStream = InstallPayload.Resource("Vesper.Setup.Art.brand.publisher-original.png");
            using var original = new Bitmap(originalStream);
            const int sourceX = 254, sourceY = 368;
            using var orbit = original.Clone(new Rectangle(sourceX, sourceY, 746, 394), PixelFormat.Format32bppArgb);
            bool dark = _palette.IsDark;
            Filter(orbit, (x, y, pixels, i) =>
            {
                int alpha = Math.Clamp((pixels[i] - 8) * 255 / 247, 0, 255);
                float dx = x + sourceX - 625, dy = y + sourceY - 570;
                float lit = dark ? 1 : Math.Clamp((177 - MathF.Sqrt(dx * dx + dy * dy)) / 24, 0, 1);
                pixels[i] = (byte)(35 + 184 * lit);
                pixels[i + 1] = (byte)(55 + 187 * lit);
                pixels[i + 2] = (byte)(35 + 217 * lit);
                pixels[i + 3] = (byte)alpha;
            });
            Draw(graphics, orbit, new RectangleF(9, 1, 312, 165));
            return _art["emblem"] = emblem;
        }
        catch
        {
            emblem.Dispose();
            throw;
        }
    }

    private static void DrawFlight(Graphics graphics, Bitmap atlas, float elapsed)
    {
        for (int i = 0; i < Flights.Length; i++)
        {
            Flight flight = Flights[i];
            float t = elapsed / flight.Duration;
            if (t >= 1) continue;

            // Shrinking depth enlarges the wings and carries them past the same viewpoint.
            float depth = 1 - .96f * t * (.72f + .28f * t);
            float scale = 1 / depth;
            float pass = Math.Max(0, (t - .79f) / .21f);
            float bank = flight.Bank * pass * pass;
            float sway = MathF.Sin(elapsed * 4 + i * 1.7f);
            float x = 400 + (flight.X + 330 * bank + 5 * sway) * scale;
            float y = 237 + (flight.Y + 6 * MathF.Cos(elapsed * 3.5f + i)) * scale;
            float size = flight.Size * scale;
            float margin = .65f * size;
            if (x < -margin || x > LogicalWidth + margin || y < -margin || y > LogicalHeight + margin) continue;

            int frame = 33 + ((int)(elapsed * (16 + i % 4) + i * 3) % 10);
            DrawButterfly(graphics, atlas, x, y, size, flight.Bank + bank * .5f + .10f * sway, frame);
        }
    }

    private static void DrawButterfly(Graphics graphics, Bitmap atlas, float x, float y, float size, float tilt, int frame)
    {
        var saved = graphics.Save();
        graphics.TranslateTransform(x, y);
        graphics.RotateTransform(tilt * 180 / MathF.PI);
        graphics.DrawImage(atlas, new RectangleF(-size / 2, -size / 2, size, size),
            new RectangleF(frame % 10 * 80, frame / 10 * 80, 80, 80), GraphicsUnit.Pixel);
        graphics.Restore(saved);
    }

    private readonly record struct Flight(float X, float Y, float Size, float Duration, float Bank);

    private Bitmap Art(string name, bool feather = false)
    {
        if (_art.TryGetValue(name, out var cached)) return cached;
        Bitmap bitmap = Load(name);
        if (feather)
            Filter(bitmap, (x, y, pixels, i) =>
            {
                float edge = Math.Min(x, bitmap.Width - 1 - x) / (bitmap.Width * .15f);
                float fade = Math.Min(edge, y / (bitmap.Height * .10f));
                pixels[i + 3] = (byte)(pixels[i + 3] * Math.Clamp(fade, 0, 1));
            });
        return _art[name] = bitmap;
    }

    private static Bitmap Load(string name)
    {
        using Stream stream = InstallPayload.Resource($"Vesper.Setup.Art.{name}.png");
        using var image = new Bitmap(stream);
        return image.Clone(new Rectangle(Point.Empty, image.Size), PixelFormat.Format32bppArgb);
    }

    private static void Filter(Bitmap bitmap, Action<int, int, byte[], int> pixel)
    {
        BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[bitmap.Width * 4];
            for (int y = 0; y < bitmap.Height; y++)
            {
                IntPtr start = data.Scan0 + y * data.Stride;
                Marshal.Copy(start, row, 0, row.Length);
                for (int x = 0; x < bitmap.Width; x++) pixel(x, y, row, x * 4);
                Marshal.Copy(row, 0, start, row.Length);
            }
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static void Center(Graphics graphics, Bitmap bitmap, float x, float y, float width, float height, float opacity = 1)
    {
        float scale = Math.Min(width / bitmap.Width, height / bitmap.Height);
        width = bitmap.Width * scale;
        height = bitmap.Height * scale;
        Draw(graphics, bitmap, new RectangleF(x - width / 2, y - height / 2, width, height), opacity);
    }

    private static void Draw(Graphics graphics, Bitmap bitmap, RectangleF rect, float opacity = 1)
    {
        if (opacity <= 0) return;
        if (opacity >= .999f) graphics.DrawImage(bitmap, rect);
        else
        {
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
            graphics.DrawImage(bitmap, Rectangle.Round(rect), 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
        }
    }

    private void ClearArt()
    {
        foreach (Bitmap image in _art.Values) image.Dispose();
        _art.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            ClearArt();
            _heading.Dispose();
            _subtitle.Dispose();
        }
        base.Dispose(disposing);
    }
}
