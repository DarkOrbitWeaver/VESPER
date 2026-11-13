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
    internal const double PublisherStart = 2.91;
    internal const double BlankStart = 5.63;
    internal const double Duration = 6.0;
    private const double SwarmStart = 1.18;
    private const double WordmarkStart = 3.73;
    private const double HoldStart = 4.88;
    private const double FadeStart = 5.35;
    private static readonly double[] Cuts = [0, .12, .26, .43, .57, .70, .88, 1.04];
    private static readonly string[] Props = ["01_hourglass", "02_key", "03_letter", "04_quill", "05_camera", "06_vinyl", "07_book", "08_moon"];
    private static readonly Flight[] Flights = Enumerable.Range(0, 26).Select(CreateFlight).ToArray();

    private readonly Font _heading = new("Georgia", 56, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _subtitle = new("Georgia", 11, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Dictionary<string, Bitmap> _art = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private VesperPalette _palette;
    private int _scene = -1;

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
            if (_clock.Elapsed.TotalSeconds >= Duration)
            {
                StopIntro();
                IntroFinished?.Invoke();
            }
            else Invalidate();
        };
    }

    internal void SetPalette(VesperPalette palette)
    {
        _palette = palette;
        ClearArt();
        Invalidate();
    }

    internal void StartIntro()
    {
        ClearArt();
        _clock.Restart();
        _timer.Start();
        Invalidate();
    }

    internal void StopIntro()
    {
        if (!Playing) return;
        _timer.Stop();
        _clock.Stop();
        ClearArt();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics, ClientSize, Playing ? _clock.Elapsed.TotalSeconds : null);

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
            else DrawWelcome(graphics);
        }
        finally { graphics.Restore(saved); }
    }

    private void DrawWelcome(Graphics graphics)
    {
        EnterScene(0);
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
        if (time < SwarmStart)
        {
            int index = Math.Max(0, Array.FindLastIndex(Cuts, cut => cut <= time));
            EnterScene(index + 1);
            Center(graphics, Art("objects." + Props[index]), 400, 236, 224, 220);
            return;
        }
        if (time < PublisherStart)
        {
            EnterScene(9);
            DrawSwarm(graphics, time, Art("atlas"));
            return;
        }
        if (time >= BlankStart)
        {
            EnterScene(11);
            return;
        }

        EnterScene(10);
        string theme = _palette.IsDark ? "dark" : "light";
        float fade = 1 - Math.Clamp((float)((time - FadeStart) / (BlankStart - FadeStart)), 0, 1);
        float reveal = Math.Clamp((float)((time - PublisherStart) / (WordmarkStart - PublisherStart)), 0, 1);
        Bitmap emblem = Emblem();
        Draw(graphics, emblem, new RectangleF(235, 109, emblem.Width, emblem.Height), reveal * fade);
        if (time < WordmarkStart) return;

        Bitmap wordmark = Art("brand.wordmark_" + theme);
        float fraction = MathF.Ceiling(Math.Clamp((float)((time - WordmarkStart) / (HoldStart - WordmarkStart)), 0, 1) * 16) / 16;
        float height = 376f * wordmark.Height / wordmark.Width;
        const float wordmarkY = 323;
        var saved = graphics.Save();
        graphics.SetClip(new RectangleF(212, wordmarkY, 376 * fraction, height + 1));
        Draw(graphics, wordmark, new RectangleF(212, wordmarkY, 376, height), fade);
        graphics.Restore(saved);
    }

    private Bitmap Emblem()
    {
        if (_art.TryGetValue("emblem", out var cached)) return cached;
        var emblem = new Bitmap(330, 170, PixelFormat.Format32bppPArgb);
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

    private static void DrawSwarm(Graphics graphics, double time, Bitmap atlas)
    {
        for (int i = 0; i < Flights.Length; i++)
        {
            Flight flight = Flights[i];
            float t = (float)((time - flight.Start) / flight.Duration);
            if (t is < 0 or > 1) continue;
            float x = flight.From.X + (flight.To.X - flight.From.X) * t;
            float y = flight.From.Y + (flight.To.Y - flight.From.Y) * t + MathF.Sin((t * 4 + flight.Phase) * MathF.PI) * flight.Wiggle;
            float size = 91 * flight.Scale * (.58f + .55f * t);
            int frame = 33 + ((int)((time - flight.Start) * 11 + i * 3) % 10);
            var saved = graphics.Save();
            graphics.TranslateTransform(x, y);
            graphics.RotateTransform((flight.Tilt + MathF.Sin(t * 6 + flight.Phase) * .14f) * 180 / MathF.PI);
            if (i % 6 == 0) graphics.ScaleTransform(-1, 1);
            graphics.DrawImage(atlas, new RectangleF(-size / 2, -size / 2, size, size),
                new RectangleF(frame % 10 * 80, frame / 10 * 80, 80, 80), GraphicsUnit.Pixel);
            graphics.Restore(saved);
        }
    }

    private readonly record struct Flight(PointF From, PointF To, double Start, double Duration, float Scale, float Wiggle, float Phase, float Tilt);

    private static Flight CreateFlight(int i)
    {
        float u = RandomValue(i * 13 + 8), v = RandomValue(i * 19 + 21), w = RandomValue(i * 23 + 3);
        (PointF from, PointF to) = (i % 4) switch
        {
            0 => (new PointF(-115, u * 550), new PointF(935, v * 460)),
            1 => (new PointF(920, u * 480), new PointF(-130, v * 480)),
            2 => (new PointF(u * 800, -120), new PointF(v * 800, 600)),
            _ => (new PointF(u * 800, 575), new PointF(v * 800, -135))
        };
        double start = SwarmStart + i * .046;
        return new(from, to, start, Math.Min(.48 + w * .48, PublisherStart - start - .02),
            .62f + w * .92f, 8 + w * 17, w * 5, i % 2 == 0 ? .16f : -.16f);
    }

    private static float RandomValue(int input)
    {
        uint value = unchecked((uint)input * 1664525u + 1013904223u);
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        return (value & 0xffffff) / 16777216f;
    }

    private void EnterScene(int scene)
    {
        if (scene == _scene) return;
        ClearArt();
        _scene = scene;
    }

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
        _scene = -1;
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
