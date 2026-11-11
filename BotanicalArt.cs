using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VESPER;

// A shared set of small decorations, used only on the UI thread. Decode individual assets
// on demand and release them when the last Botanical window closes or switches to plain.
internal sealed class BotanicalArt : IDisposable
{
    private static Artwork? _shared;
    private static int _users;
    private readonly Artwork _art;
    private bool _disposed;

    private BotanicalArt()
    {
        _art = _shared ??= new Artwork();
        _users++;
    }

    internal static BotanicalArt? Acquire(VesperPalette palette) => palette.Botanical ? new BotanicalArt() : null;

    internal Image? Butterfly => _art.Butterfly.Value;
    internal Image? EmptyHero => _art.Hero.Value;

    internal void PaintHeader(Graphics graphics, Rectangle bounds, Rectangle clip, Rectangle artArea, bool dialog)
    {
        if (bounds.IsEmpty || clip.IsEmpty) return;
        using (var grain = new TextureBrush(_art.Grain.Value))
            graphics.FillRectangle(grain, Rectangle.Intersect(bounds, clip));

        if (artArea.Width <= 0 || artArea.Height <= 0 || !clip.IntersectsWith(artArea)) return;
        Image? branch = (dialog ? _art.Sprig : _art.Branch).Value;
        if (branch is null) return;
        float scale = Math.Min((float)artArea.Width / branch.Width, (float)artArea.Height / branch.Height);
        int width = (int)Math.Round(branch.Width * scale);
        int height = (int)Math.Round(branch.Height * scale);
        if (width < 1 || height < 1) return;
        var target = new Rectangle(artArea.Right - width, artArea.Y + (artArea.Height - height) / 2, width, height);
        DrawArtwork(graphics, branch, target);
    }

    internal void PaintLandscape(Graphics graphics, Rectangle bounds, bool dark)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        DrawArtwork(graphics, (dark ? _art.DarkHills : _art.LightHills).Value, bounds);
    }

    private static void DrawArtwork(Graphics graphics, Image image, Rectangle bounds)
    {
        InterpolationMode interpolation = graphics.InterpolationMode;
        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        graphics.DrawImage(image, bounds);
        graphics.InterpolationMode = interpolation;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (--_users != 0) return;
        _art.Dispose();
        _shared = null;
    }

    private sealed class Artwork : IDisposable
    {
        internal Lazy<Bitmap> Grain { get; } = new(CreateGrain);
        internal Lazy<Bitmap?> Butterfly { get; } = new(() => Load("butterfly.png"));
        internal Lazy<Bitmap?> Hero { get; } = new(() => Load("empty-hero.png", side: 44, vertical: 34));
        internal Lazy<Bitmap?> Branch { get; } = new(() => Load("header.png", side: 12, vertical: 22));
        internal Lazy<Bitmap?> Sprig { get; } = new(() => Load("sprig.png", side: 12, vertical: 20));
        internal Lazy<Bitmap> LightHills { get; } = new(() => CreateLandscape(dark: false));
        internal Lazy<Bitmap> DarkHills { get; } = new(() => CreateLandscape(dark: true));

        public void Dispose()
        {
            if (Grain.IsValueCreated) Grain.Value.Dispose();
            if (Butterfly.IsValueCreated) Butterfly.Value?.Dispose();
            if (Hero.IsValueCreated) Hero.Value?.Dispose();
            if (Branch.IsValueCreated) Branch.Value?.Dispose();
            if (Sprig.IsValueCreated) Sprig.Value?.Dispose();
            if (LightHills.IsValueCreated) LightHills.Value.Dispose();
            if (DarkHills.IsValueCreated) DarkHills.Value.Dispose();
        }
    }

    private static Bitmap CreateGrain()
    {
        // A few alpha levels of paper texture. The base color is supplied by the actual
        // surface, so tiled texture and transparent children share the same background.
        var tile = new Bitmap(128, 96, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(tile);
        using var shade = new SolidBrush(Color.FromArgb(6, 112, 90, 57));
        using var light = new SolidBrush(Color.FromArgb(9, 255, 251, 233));
        var random = new Random(1723);
        for (int i = 0; i < 3200; i++)
            graphics.FillRectangle(i % 2 == 0 ? shade : light, random.Next(tile.Width), random.Next(tile.Height), 1, 1);
        return tile;
    }

    private static Bitmap CreateLandscape(bool dark)
    {
        // Two feathered watercolors overlap on a wide, transparent strip. No edge of either
        // source image marks the point where the landscape begins on the footer.
        var scene = new Bitmap(1440, 96, PixelFormat.Format32bppPArgb);
        using var left = Load("hills-left.png", side: 82, vertical: 12);
        using var right = Load("hills.png", side: 62, vertical: 12);
        using var graphics = Graphics.FromImage(scene);
        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        using var blend = new ImageAttributes();
        if (left is not null)
        {
            blend.SetColorMatrix(new ColorMatrix { Matrix33 = dark ? 0.21f : 0.56f });
            graphics.DrawImage(left, new Rectangle(0, 10, 960, 86), 0, 0, left.Width, left.Height, GraphicsUnit.Pixel, blend);
        }
        if (right is not null)
        {
            blend.SetColorMatrix(new ColorMatrix { Matrix33 = dark ? 0.34f : 0.74f });
            graphics.DrawImage(right, new Rectangle(460, 0, 980, 96), 0, 0, right.Width, right.Height, GraphicsUnit.Pixel, blend);
        }
        return scene;
    }

    private static Bitmap? Load(string name, int side = 0, int vertical = 0)
    {
        using Image? source = UiKit.LoadImage(Path.Combine("Botanical", name));
        // Four decoded sources at this limit plus both scenery strips stay below 5 MiB.
        if (source is null || (long)source.Width * source.Height > 240_000) return null;
        var image = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(image)) graphics.DrawImageUnscaled(source, 0, 0);
        if (side > 0 || vertical > 0) FeatherEdges(image, side, vertical);
        return image;
    }

    private static void FeatherEdges(Bitmap image, int side, int vertical)
    {
        var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadWrite, image.PixelFormat);
        try
        {
            var horizontal = new float[image.Width];
            for (int x = 0; x < horizontal.Length; x++)
                horizontal[x] = Fade(Math.Min(x, image.Width - 1 - x), side);
            var row = new byte[image.Width * 4];
            for (int y = 0; y < image.Height; y++)
            {
                float alpha = Fade(Math.Min(y, image.Height - 1 - y), vertical);
                IntPtr start = data.Scan0 + y * data.Stride;
                Marshal.Copy(start, row, 0, row.Length);
                for (int x = 0; x < horizontal.Length; x++)
                {
                    int offset = x * 4 + 3;
                    row[offset] = (byte)Math.Round(row[offset] * horizontal[x] * alpha);
                }
                Marshal.Copy(row, 0, start, row.Length);
            }
        }
        finally { image.UnlockBits(data); }
    }

    private static float Fade(int distance, int length)
    {
        if (length <= 0 || distance >= length) return 1;
        float fraction = (float)distance / length;
        return fraction * fraction * (3 - 2 * fraction);
    }
}
