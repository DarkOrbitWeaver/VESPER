using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Vesper.Engine;
using Vesper.MediaResolver.Models;

namespace VESPER;

internal static class BotanicalPreview
{
    private static readonly Uri Page = new("https://example.com/forest-journal");

    internal static void Run(string[] args)
    {
        int capture = Array.IndexOf(args, "--capture");
        if (capture >= 0)
        {
            if (capture + 1 == args.Length) throw new ArgumentException("Specify a screenshot folder.");
            Capture(Path.GetFullPath(args[capture + 1]));
            return;
        }

        var prefs = new VesperPreferences { Theme = args.Contains("--dark") ? ThemeChoice.Dark : ThemeChoice.Light };
        using var main = CreateMain(prefs, botanical: true);
        main.Text = "VESPER — Botanical preview";
        main.LoadPreviewRows(args.Contains("--empty") ? [] : Samples(7));
        main.PreviewAddRequested += () =>
        {
            using var media = new MediaDiscoveryForm(prefs, Page, SampleMedia());
            media.ShowDialog(main);
        };
        main.PreviewSettingsRequested += () =>
        {
            prefs.Theme = prefs.Theme == ThemeChoice.Dark ? ThemeChoice.Light : ThemeChoice.Dark;
            main.RefreshPreviewAppearance();
        };
        Application.Run(main);
    }

    private static Form1 CreateMain(VesperPreferences prefs, bool botanical) =>
        new(prefs, null, startHidden: false, botanicalPreview: botanical) { ClientSize = new Size(1040, 610) };

    private static void Capture(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var theme in new[] { ThemeChoice.Light, ThemeChoice.Dark })
        {
            string name = theme.ToString().ToLowerInvariant();
            var prefs = new VesperPreferences { Theme = theme };
            using var main = CreateMain(prefs, botanical: false);
            main.LoadPreviewRows(Samples(7));
            ShowOffscreen(main);
            Save(main, folder, $"main-{name}");
            main.LoadPreviewRows([]);
            Save(main, folder, $"empty-{name}");

            using var media = new MediaDiscoveryForm(prefs, Page, SampleMedia());
            ShowOffscreen(media);
            Save(media, folder, $"media-{name}");
            media.SetPreviewState(1);
            Save(media, folder, $"media-{name}-focused");
            media.SetPreviewState(1, pressed: true);
            Save(media, folder, $"media-{name}-pressed");
            media.SetPreviewState(4);
            Save(media, folder, $"media-{name}-formats");
            using var limited = new MediaDiscoveryForm(prefs, Page, SampleMedia(includeAudio: false));
            ShowOffscreen(limited);
            limited.SetPreviewState(3);
            Save(limited, folder, $"media-{name}-disabled");
        }

        File.WriteAllText(Path.Combine(folder, "preview-metrics.json"),
            JsonSerializer.Serialize(Measure(botanical: false), new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void ShowOffscreen(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(SystemInformation.VirtualScreen.Left - form.Width - 32, SystemInformation.VirtualScreen.Top);
        form.Show();
        Application.DoEvents();
    }

    private static void ShowForMeasure(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(SystemInformation.VirtualScreen.Left + 8, SystemInformation.VirtualScreen.Top + 8);
        form.Show();
        Application.DoEvents();
    }

    private static void Save(Control control, string folder, string name)
    {
        control.Refresh();
        using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(folder, name + ".png"), ImageFormat.Png);
    }

    private static object Measure(bool botanical)
    {
        var prefs = new VesperPreferences();
        DownloadItem[] rows = Samples(10_000);
        var start = new List<double>();
        using var process = Process.GetCurrentProcess();
        uint gdiStart = 0;
        for (int i = 0; i < 23; i++)
        {
            var sw = Stopwatch.StartNew();
            using (var form = CreateMain(prefs, botanical))
            {
                form.LoadPreviewRows(rows);
                ShowOffscreen(form);
                form.Refresh();
                if (i >= 3) start.Add(sw.Elapsed.TotalMilliseconds);
            }
            if (i == 2) gdiStart = GetGuiResources(process.Handle, 0);
        }

        uint gdiEnd = GetGuiResources(process.Handle, 0);
        using var main = CreateMain(prefs, botanical);
        main.LoadPreviewRows(rows);
        ShowForMeasure(main);
        var grid = Descendants(main).OfType<DownloadGrid>().Single();

        using var bitmap = new Bitmap(grid.Width, grid.Height);
        var paint = new List<double>();
        for (int i = 0; i < 130; i++)
        {
            grid.AutoScrollPosition = new Point(0, i * 96);
            var sw = Stopwatch.StartNew();
            grid.DrawToBitmap(bitmap, grid.ClientRectangle);
            if (i >= 10) paint.Add(sw.Elapsed.TotalMilliseconds);
        }

        int gridPaints = 0;
        grid.Paint += (_, _) => gridPaints++;
        grid.AutoScrollPosition = Point.Empty;
        Application.DoEvents();
        gridPaints = 0;

        var scroll = new List<double>();
        for (int i = 0; i < 120; i++)
        {
            int beforePaints = gridPaints;
            var sw = Stopwatch.StartNew();
            grid.AutoScrollPosition = new Point(0, (i + 1) * 96);
            grid.Invalidate();
            grid.Update();
            Application.DoEvents();
            sw.Stop();
            if (gridPaints == beforePaints)
                throw new InvalidOperationException("A preview scroll did not paint the download grid.");
            if (i >= 10) scroll.Add(sw.Elapsed.TotalMilliseconds);
        }

        grid.AutoScrollPosition = Point.Empty;
        Application.DoEvents();
        DownloadItem[] activeItems = grid.VisibleItems.Take(3).ToArray();
        if (activeItems.Length != 3)
            throw new InvalidOperationException("The preview needs three visible rows for a progress batch.");

        for (int i = 0; i < activeItems.Length; i++)
            activeItems[i].UpdateFrom(ProgressSnapshot(activeItems[i], 10_000_000 + i * 1_000_000));
        grid.Refresh();
        Application.DoEvents();

        var progress = new List<double>();
        var progressBatch = new JobSnapshot[activeItems.Length];
        for (int sample = 0; sample < 80; sample++)
        {
            for (int i = 0; i < activeItems.Length; i++)
                progressBatch[i] = ProgressSnapshot(activeItems[i], (sample + 11) * 1_000_000L + i * 100_000L);

            int beforePaints = gridPaints;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < activeItems.Length; i++)
                activeItems[i].UpdateFrom(progressBatch[i]);
            grid.RefreshRows(progressBatch, static snapshot => snapshot.Id);
            grid.Update();
            Application.DoEvents();
            sw.Stop();
            if (gridPaints == beforePaints)
                throw new InvalidOperationException("A preview progress batch did not paint the download grid.");
            if (sample >= 8) progress.Add(sw.Elapsed.TotalMilliseconds);
        }

        start.Sort();
        paint.Sort();
        (double ScrollP50Ms, double ScrollP95Ms) = Percentiles(scroll);
        (double ProgressP50Ms, double ProgressP95Ms) = Percentiles(progress);
        return new
        {
            Rows = rows.Length,
            WarmFormMedianMs = Math.Round((start[9] + start[10]) / 2, 2),
            GridDrawP95Ms = Math.Round(paint[113], 2),
            ScrollSamples = scroll.Count,
            GridScrollP50Ms = ScrollP50Ms,
            GridScrollP95Ms = ScrollP95Ms,
            ProgressBatchSize = activeItems.Length,
            ProgressSamples = progress.Count,
            ProgressBatchP50Ms = ProgressP50Ms,
            ProgressBatchP95Ms = ProgressP95Ms,
            GdiChangeAfter20MainWindows = (int)gdiEnd - (int)gdiStart
        };
    }

    private static (double P50, double P95) Percentiles(List<double> samples)
    {
        samples.Sort();
        int middle = samples.Count / 2;
        double p50 = samples.Count % 2 == 0
            ? (samples[middle - 1] + samples[middle]) / 2
            : samples[middle];
        int p95 = Math.Clamp((int)Math.Ceiling(samples.Count * 0.95) - 1, 0, samples.Count - 1);
        return (Math.Round(p50, 2), Math.Round(samples[p95], 2));
    }

    private static JobSnapshot ProgressSnapshot(DownloadItem item, long progressBytes) => new()
    {
        Id = item.Id,
        Kind = JobKind.Http,
        FileName = item.Name,
        SourceUrl = item.SourceUrl,
        Destination = item.Destination,
        State = JobState.Downloading,
        TotalBytes = 100_000_000,
        ProgressBytes = progressBytes,
        SpeedBytesPerSecond = 1_250_000 + progressBytes / 10,
        Eta = TimeSpan.FromSeconds(80),
        CreatedUtc = item.CreatedUtc
    };

    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    private static DownloadItem[] Samples(int count)
    {
        string[] names = ["Forest Journal - A Calm Morning.mp4", "Botanical Archive.zip", "Field Recordings.flac",
            "Windows Tools.iso", "Expedition Notes.pdf", "Orchard Collection.zip", "Spring Garden.mp4"];
        JobStatus[] states = [JobStatus.Downloading, JobStatus.Queued, JobStatus.Paused, JobStatus.Completed,
            JobStatus.Completed, JobStatus.NeedsLink, JobStatus.Downloading];
        return Enumerable.Range(0, count).Select(i =>
        {
            int n = i % names.Length;
            return new DownloadItem { Id = Guid.NewGuid(), Name = names[n],
                Extension = Path.GetExtension(names[n]).TrimStart('.').ToUpperInvariant(),
                FileType = n is 0 or 6 ? "Video" : n == 2 ? "Audio" : "File",
                Size = n == 0 ? "1.72 GB" : "124 MB", Status = states[n],
                Percent = n is 0 or 2 or 6 ? 68 - n * 5 : null,
                Transfer = n is 0 or 6 ? "11.4 MB/s" : "", Eta = n is 0 or 6 ? "2 min left" : "" };
        }).ToArray();
    }

    private static MediaItem SampleMedia(bool includeAudio = true)
    {
        var tracks = new List<MediaRepresentation>();
        foreach (int height in new[] { 2160, 1080, 720, 480 })
        {
            tracks.Add(new MediaRepresentation { Id = $"v{height}", Url = new(Page, $"/{height}.mp4"),
                Content = StreamContent.VideoAndAudio, Container = "mp4", VideoCodec = "h264", AudioCodec = "aac",
                Height = height, FileSize = height * 500_000L, BitrateKbps = height * 4 });
        }
        if (includeAudio)
            tracks.Add(new MediaRepresentation { Id = "audio", Url = new(Page, "/audio.m4a"),
                Content = StreamContent.AudioOnly, Container = "m4a", AudioCodec = "aac", BitrateKbps = 128,
                FileSize = 18_000_000 });
        return new MediaItem { SourceUrl = Page, Provider = "Preview", MediaId = "forest", Title = "Forest Journal — A Calm Morning",
            Duration = TimeSpan.FromMinutes(26), Representations = tracks };
    }
}
