using System.Diagnostics;
using System.Drawing.Drawing2D;
using static VESPER.MediaTransferBridge;
using Vesper.Engine;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Playlists;
using QualityMode = Vesper.MediaResolver.Formats.QualityMode;

namespace VESPER;

/// <summary>
/// Event-driven media discovery. Uses the same WinForms visual language as other VESPER popups.
/// Discovery and thumbnail loading stop when the window closes; no idle timers or polling.
/// </summary>
[System.ComponentModel.DesignerCategory("Form")]
internal sealed class MediaDiscoveryForm : VesperPopupForm
{
    private enum ViewState { Inspecting, Ready, Unavailable, Collection }

    private readonly Uri _url;
    private IReadOnlyList<Uri>? _pastedUrls;
    private BrowserDiscoveryMessage? _browserContext;
    private readonly VesperPalette _colors;
    private readonly DefaultMediaResolver _resolver = new(EngineHost.MediaNetwork());
    private readonly SecureHttpClient _thumbnails = new(EngineHost.MediaNetwork(maxConcurrent: 1));
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Panel _body;
    private readonly Panel _canvas;
    private readonly Panel _footer;
    private readonly Label _heading;
    private readonly Font _headingFont;
    private readonly Panel _posterFrame;
    private Bitmap? _poster;
    private readonly Label _title;
    private readonly Label _source;
    private readonly Label _status;
    private readonly Label _notice;
    private readonly Label _qualityLabel;
    private readonly Label _customLabel;
    private readonly VesperChoice _quality;
    private readonly VesperChoice _outputChoice;
    private readonly VesperChoice _audioOutputChoice;
    private readonly VesperChoice _customFormat;
    private readonly CheckedListBox _entries;
    private readonly VesperButton _download;
    private readonly VesperButton _queue;
    private readonly VesperButton _retry;
    private readonly VesperButton _collectionButton;
    private readonly VesperButton _viewPage;
    private readonly VesperButton _cancel;
    private readonly List<MediaRepresentation> _playableFormats = [];
    private readonly Panel _presetOptions;
    private readonly List<MediaQualityOption> _presets = [];
    private readonly MediaItem? _sample;

    private MediaItem? _item;
    private List<CollectionEntry>? _collection;
    private ViewState _state = ViewState.Inspecting;
    private bool _busy;
    private bool _truncated;
    private bool _hasMetadata;
    private bool _canChooseM4a;
    private bool _arranging;
    private int _presetIndex;
    private CancellationTokenSource? _attempt;
    private CancellationTokenSource? _posterLoad;
    private Uri? _posterUrl;
    private readonly Dictionary<Uri, byte[]> _posterCache = new();

    public IReadOnlyList<MediaDownloadChoice> Selections { get; private set; } = [];
    public bool QueueOnly { get; private set; }

    public MediaDiscoveryForm(VesperPreferences preferences, Uri url, MediaItem? sample = null)
    {
        _url = url;
        _sample = sample;
        _colors = UiKit.Palette(preferences);
        VesperWindowChrome.Brand(this);
        Text = "Discover media — VESPER";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(650, 366 + VesperDialogChrome.HeaderHeight);
        Font = new Font("Segoe UI", 9F);
        BackColor = _colors.Surface;
        ForeColor = _colors.Text;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;

        _body = new VesperScrollPanel { Dock = DockStyle.Fill, BackColor = _colors.Surface, AutoScroll = true, TabIndex = 0 };
        _canvas = new Panel { Dock = DockStyle.Top, BackColor = _colors.Surface };
        _body.Controls.Add(_canvas);
        _footer = new VesperPanel { Dock = DockStyle.Bottom, Height = 62, BackColor = _colors.Surface };
        _footer.Paint += (_, e) =>
        {
            using var line = new Pen(_colors.Divider);
            e.Graphics.DrawLine(line, 0, 0, _footer.Width, 0);
        };

        _headingFont = UiKit.Heading(_colors, 18);
        _heading = new Label
        {
            Text = "Find media", Font = _headingFont, ForeColor = _colors.Text,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _canvas.Controls.Add(_heading);

        _posterFrame = new VesperPanel { BackColor = _colors.Surface, AccessibleName = "Media thumbnail", TabStop = false };
        _posterFrame.Paint += (_, e) => PaintPoster(e.Graphics);
        _canvas.Controls.Add(_posterFrame);

        _title = L("Checking link…", 11, bold: true);
        _title.AutoEllipsis = true;
        _source = L($"{_url.Host} · Media source", 8.5f, muted: true);
        _status = L("Looking for available media. This may take a moment.", 9, muted: true);
        _status.TextAlign = ContentAlignment.TopLeft;
        _status.AutoEllipsis = false;
        _notice = L("", 8.5f, muted: true);
        _notice.TextAlign = ContentAlignment.TopLeft;
        _notice.AutoEllipsis = false;
        _canvas.Controls.AddRange([_title, _source, _status, _notice]);

        _viewPage = B("View original", 128, symbol: VesperIcon.Link, quiet: true);
        _viewPage.TextAlign = ContentAlignment.MiddleLeft;
        _viewPage.Click += (_, _) => OpenOriginal();
        _canvas.Controls.Add(_viewPage);

        _qualityLabel = L("Quality", 9, bold: true);
        _quality = new VesperChoice { AccessibleName = "Playlist quality", Symbol = VesperIcon.Quality };
        _quality.ApplyPalette(_colors);
        _quality.SetItems("Best available with audio", "Balanced", "Smallest suitable", "Audio only");
        _outputChoice = new VesperChoice { AccessibleName = "Output format preference" };
        _outputChoice.ApplyPalette(_colors);
        _outputChoice.SetItems("Compatible", "Original");
        _audioOutputChoice = new VesperChoice { AccessibleName = "Audio output format" };
        _audioOutputChoice.ApplyPalette(_colors);
        _audioOutputChoice.SetItems("M4A (AAC)", "Original audio");
        _customLabel = L("Exact available format", 8.5f, muted: true);
        _customFormat = new VesperChoice { AccessibleName = "Exact format", Symbol = VesperIcon.Layers };
        _customFormat.ApplyPalette(_colors);
        _customFormat.SelectedIndexChanged += (_, _) => UpdateChoiceSummary();
        _canvas.Controls.AddRange([_qualityLabel, _quality, _outputChoice, _audioOutputChoice, _customLabel, _customFormat]);
        _presetOptions = new Panel { BackColor = _colors.Surface, Visible = false, TabIndex = 3 };
        (string Title, VesperIcon Icon)[] presets =
        [
            ("Best quality", VesperIcon.Crown), ("Balanced", VesperIcon.Quality), ("Smallest", VesperIcon.Leaf),
            ("Audio only", VesperIcon.Audio), ("More formats", VesperIcon.Layers)
        ];
        for (int i = 0; i < presets.Length; i++)
        {
            var (title, icon) = presets[i];
            var option = new MediaQualityOption(title, icon, _colors) { TabIndex = i };
            _presets.Add(option);
            _presetOptions.Controls.Add(option);
        }
        _presets[0].Checked = true;
        _canvas.Controls.Add(_presetOptions);
        void OutputChanged(object? sender, EventArgs e)
        {
            if (_state != ViewState.Ready || _item is null) return;
            RefreshPresets(_item);
            UpdateChoiceSummary();
            FitToContent();
        }
        _outputChoice.SelectedIndexChanged += OutputChanged;
        _audioOutputChoice.SelectedIndexChanged += OutputChanged;
        _quality.SelectedIndexChanged += (_, _) => { if (_state == ViewState.Collection) UpdateQualityUi(); };

        _collectionButton = B("Browse playlist", 152, VesperIcon.Queue);
        _collectionButton.Click += async (_, _) => await LoadCollectionAsync();
        _canvas.Controls.Add(_collectionButton);

        _entries = new VesperCheckedList(_colors)
        {
            CheckOnClick = true, BorderStyle = BorderStyle.None,
            BackColor = _colors.Surface, ForeColor = _colors.Text,
            Font = new Font("Segoe UI", 9), IntegralHeight = false, HorizontalScrollbar = true
        };
        _entries.ItemCheck += (_, _) => BeginInvoke(() => UpdateCollectionActions());
        _canvas.Controls.Add(_entries);

        _cancel = B("Cancel", 88);
        _cancel.DialogResult = DialogResult.Cancel;
        _queue = B("Add to queue", 122, VesperIcon.Queue);
        _download = B("Download", 130, VesperIcon.Download, primary: true);
        _retry = B("Try again", 122, VesperIcon.Refresh);
        _queue.Click += async (_, _) => await MakeSelectionsAsync(queueOnly: true);
        _download.Click += async (_, _) => await MakeSelectionsAsync(queueOnly: false);
        _retry.Click += async (_, _) => await DiscoverAsync();
        _footer.Controls.AddRange([_cancel, _queue, _download, _retry]);
        for (int i = 0; i < _presets.Count; i++)
        {
            int index = i;
            _presets[i].CheckedChanged += (_, _) =>
            {
                if (_presets[index].Checked) SelectPreset(index);
            };
        }

        _body.Resize += (_, _) => LayoutBody();
        _canvas.Resize += (_, _) => LayoutBody();
        _footer.Resize += (_, _) => LayoutFooter();
        Controls.Add(_body);
        Controls.Add(_footer);
        VesperDialogChrome.Attach(this, "VESPER  /  Discover media", _colors, VesperIcon.Video);
        CancelButton = _cancel;
        AcceptButton = _download;
        Render(ViewState.Inspecting);
        Shown += async (_, _) =>
        {
            FitToContent();
            if (_sample is null) { await DiscoverAsync(); return; }
            SetBusy(true, "Checking available media…");
            await Task.Yield();
            if (IsDisposed || _lifetime.IsCancellationRequested) return;
            DisplayItem(_sample);
            SetBusy(false);
        };
        DpiChanged += (_, _) => BeginInvoke(() => { if (!IsDisposed) FitToContent(); });
        FormClosing += (_, _) => { _lifetime.Cancel(); _attempt?.Cancel(); _posterLoad?.Cancel(); };
        Disposed += (_, _) =>
        {
            _lifetime.Cancel();
            _poster?.Dispose();
            _headingFont.Dispose();
            _resolver.Dispose();
            _thumbnails.Dispose();
            _attempt?.Dispose();
            _posterLoad?.Dispose();
            _lifetime.Dispose();
        };
    }

    public MediaDiscoveryForm(VesperPreferences preferences, Uri url, BrowserDiscoveryMessage browser)
        : this(preferences, url)
    {
        _browserContext = browser;
    }

    public MediaDiscoveryForm(VesperPreferences preferences, IReadOnlyList<Uri> urls)
        : this(preferences, urls.Count > 0 ? urls[0] : throw new ArgumentException("At least one link required", nameof(urls)))
    {
        // Limit is explicit. Never silently discard pasted URLs.
        if (urls.Count > 200) throw new ArgumentException("Select at most 200 links per batch.", nameof(urls));
        _pastedUrls = urls;
    }

    private Label L(string text, float size, bool bold = false, bool muted = false) => new()
    {
        Text = text, AutoSize = false,
        ForeColor = muted ? _colors.Muted : _colors.Text,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private VesperButton B(string text, int width, VesperIcon symbol = VesperIcon.None, bool primary = false, bool quiet = false)
    {
        var button = new VesperButton { Text = text, Width = width, Height = 35, Primary = primary, Quiet = quiet, Symbol = symbol };
        button.ApplyPalette(_colors);
        return button;
    }

    private void LayoutBody()
    {
        if (_arranging || _body.ClientSize.Width < S(150)) return;
        _arranging = true;
        try
        {
            _canvas.Height = S(BodyHeight());
            int width = _canvas.ClientSize.Width;
            int margin = S(24), contentWidth = Math.Max(1, width - 2 * margin);
            void Place(Control control, int x, int y, int w, int h) =>
                control.SetBounds(x, S(y), Math.Max(1, w), S(h));

            int posterWidth = S(width < S(480) ? 104 : 132);
            int textLeft = margin + posterWidth + S(17);
            int titleWidth = width - margin - textLeft;
            Place(_heading, margin, 13, contentWidth, 38);
            Place(_posterFrame, margin, 60, posterWidth, 74);
            Place(_title, textLeft, 60, titleWidth, 28);
            Place(_source, textLeft, 88, titleWidth, 19);
            Place(_viewPage, textLeft - S(13), 108, Math.Min(S(145), titleWidth), 28);

            bool playlist = _state == ViewState.Ready && _item?.Related is not null;
            int statusWidth = contentWidth - (playlist ? S(172) : 0);
            Place(_status, margin, 153, statusWidth, _state == ViewState.Unavailable ? 57 : 34);
            Place(_collectionButton, width - margin - S(162), 143, S(162), 34);
            int outputWidth = Math.Min(S(132), contentWidth / 2);
            Place(_qualityLabel, margin, 181, contentWidth - outputWidth - S(8), 26);
            Place(_outputChoice, width - margin - outputWidth, 181, outputWidth, 28);
            Place(_audioOutputChoice, width - margin - outputWidth, 181, outputWidth, 28);
            Place(_quality, margin, 211, contentWidth, 34);
            Place(_entries, margin, 256, contentWidth, 152);
            Place(_presetOptions, margin, 211, contentWidth, 181);
            for (int i = 0; i < _presets.Count; i++)
                _presets[i].SetBounds(0, S(i * 37), contentWidth, S(i < 4 ? 37 : 33));
            Place(_customLabel, margin, 398, contentWidth, 18);
            Place(_customFormat, margin, 419, contentWidth, 34);
            int noticeY = _state switch
            {
                ViewState.Ready => IsExactFormat ? 464 : 402,
                ViewState.Collection => 417,
                ViewState.Unavailable => 213,
                _ => 191
            };
            Place(_notice, margin, noticeY, contentWidth, 40);
        }
        finally { _arranging = false; }
    }

    private int S(int logicalPixels) => UiKit.Scale(this, logicalPixels);

    private void LayoutFooter()
    {
        int width = _footer.ClientSize.Width;
        int margin = S(width < S(430) ? 12 : 24), y = S(13), height = S(35);
        _cancel.SetBounds(margin, y, S(88), height);
        _download.SetBounds(width - margin - S(130), y, S(130), height);
        _queue.SetBounds(_download.Left - S(8 + 122), y, S(122), height);
        _retry.SetBounds(width - margin - S(122), y, S(122), height);
    }

    private bool IsExactFormat => _state == ViewState.Ready && _presetIndex == 4;

    private int BodyHeight() => _state switch
    {
        ViewState.Ready => IsExactFormat ? 510 : string.IsNullOrEmpty(_notice.Text) ? 407 : 448,
        ViewState.Collection => 464,
        ViewState.Unavailable => 260,
        _ => 214
    };

    private void FitToContent()
    {
        Rectangle work = Screen.FromControl(Visible ? this : Owner ?? this).WorkingArea;
        int inset = S(12);
        int desiredHeight = S(BodyHeight()) + _footer.Height + VesperDialogChrome.VerticalInset(this);
        int height = Math.Min(desiredHeight, Math.Max(1, work.Height - 2 * inset));
        int width = Math.Min(ClientSize.Width, Math.Max(1, work.Width - 2 * inset));
        if (ClientSize != new Size(width, height)) ClientSize = new Size(width, height);
        if (Visible && work.IntersectsWith(Bounds))
        {
            int x = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width));
            int y = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
            if (Location != new Point(x, y)) Location = new Point(x, y);
        }
        LayoutBody();
        LayoutFooter();
    }

    private void Render(ViewState state)
    {
        if (_state != state) _body.AutoScrollPosition = Point.Empty;
        _state = state;
        bool ready = state is ViewState.Ready;
        bool list = state is ViewState.Collection;
        _qualityLabel.Visible = ready || list;
        _quality.Visible = list;
        _presetOptions.Visible = ready;
        _collectionButton.Visible = ready && _item?.Related is not null;
        _entries.Visible = list;
        _download.Visible = _queue.Visible = ready || list;
        _retry.Visible = state == ViewState.Unavailable;
        _cancel.Text = state == ViewState.Unavailable ? "Close" : "Cancel";
        _viewPage.Visible = !list;
        _notice.Visible = state != ViewState.Inspecting;
        UpdateQualityUi();
        FitToContent();
        if (!ready) SetButtonsEnabled(list && _entries.CheckedIndices.Count > 0);
    }

    private async Task DiscoverAsync()
    {
        if (_pastedUrls is { } urls)
        {
            await DisplayCollectionAsync(new MediaCollection
            {
                SourceUrl = _url, Provider = "Pasted links", Title = "Multiple media links",
                Entries = urls.Select((u, i) => new CollectionEntry
                { MediaId = $"pasted-{i}", Url = u, Title = u.Host + u.AbsolutePath, Index = i }).ToArray(),
                IsComplete = true
            });
            return;
        }

        _attempt?.Cancel();
        _attempt?.Dispose();
        _attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken attempt = _attempt.Token;
        SetBusy(true, "Checking available media…");
        _hasMetadata = false;
        _item = null;
        _collection = null;
        _playableFormats.Clear();
        _title.Text = "Checking link…";
        _notice.Text = "";
        Render(ViewState.Inspecting);
        TryKnownYouTubeThumbnail(); // independent artwork request; never a claim of stream availability
        var updates = new Progress<DiscoveryProgress>(update =>
        {
            if (attempt.IsCancellationRequested || IsDisposed) return;
            if (!string.IsNullOrWhiteSpace(update.Title))
            { _title.Text = update.Title; _hasMetadata = true; }
            if (update.Thumbnail is not null) _ = LoadThumbnailAsync(update.Thumbnail);
            if (update.RepresentationCount > 0 && _state == ViewState.Inspecting)
                _status.Text = $"{update.RepresentationCount} source representations found; checking transfer compatibility…";
        });
        try
        {
            MediaDiscoveryResult result = await Task.Run(() => _resolver.ResolveAsync(new MediaDiscoveryRequest
            { Url = _url, Browser = _browserContext, Progress = updates,
                Options = new DiscoveryOptions { CollectionIntent = CollectionIntent.ItemOnly } }, attempt), attempt);
            if (_lifetime.IsCancellationRequested || IsDisposed) return;
            if (result.Item is { } item)
                DisplayItem(item);
            else if (result.Collection is { } collection)
                await DisplayCollectionAsync(collection);
            else
            {
                string message = result.Error?.UserMessage ?? "No downloadable media was found.";
                string hint = result.Error?.BrowserMayHelp == true
                    ? "The browser butterfly may find a stream if it is available in the player."
                    : "This page may not expose a downloadable or supported stream.";
                ShowUnavailable(message, hint);

            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (!_lifetime.IsCancellationRequested)
        {
            if (!IsDisposed) ShowUnavailable("This media source could not be inspected.",
                "Check your connection or try browser-assisted discovery.");
        }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private void DisplayItem(MediaItem item)
    {
        _item = item;
        _title.Text = string.IsNullOrWhiteSpace(item.Title) ? "Media source" : item.Title;
        _source.Text = item.Provider + (item.Duration is { } duration ? $"  ·  {duration:hh\\:mm\\:ss}" : "");
        _ = LoadThumbnailAsync(item.Thumbnail);
        UpdatePlayableFormats(item);
    }

    private void ShowUnavailable(string reason, string hint)
    {
        _status.Text = reason;
        _notice.Text = hint;
        if (_item is null && !_hasMetadata)
        {
            _title.Text = "Playback discovery unavailable";
            _source.Text = _url.Host;
        }
        Render(ViewState.Unavailable);
    }

    private void TryKnownYouTubeThumbnail()
    {
        // Public poster fallback only, not a guessed playable stream.
        string host = _url.Host.ToLowerInvariant();
        if (host is not ("youtube.com" or "www.youtube.com" or "m.youtube.com" or "youtu.be")) return;
        string[] parts = _url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? id = host == "youtu.be" ? parts.FirstOrDefault()
            : parts.Length >= 2 && parts[0] is "shorts" or "embed" or "live" ? parts[1]
            : _url.Query.TrimStart('?').Split('&')
                .FirstOrDefault(x => x.StartsWith("v=", StringComparison.OrdinalIgnoreCase))?.Substring(2);
        if (id is { Length: 11 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            _ = LoadThumbnailAsync(new Uri($"https://i.ytimg.com/vi/{id}/hqdefault.jpg"));
    }

    private void PaintPoster(Graphics graphics)
    {
        Rectangle frame = _posterFrame.ClientRectangle;
        if (_poster is not { } image)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = VesperDrawing.Rounded(frame, S(6));
            using var wash = new SolidBrush(_colors.Secondary);
            graphics.FillPath(wash, shape);
            int size = S(23);
            VesperDrawing.Icon(graphics, VesperIcon.Video,
                new Rectangle((frame.Width - size) / 2, (frame.Height - size) / 2, size, size), _colors.MutedGreen);
            return;
        }

        double ratio = Math.Min((double)frame.Width / image.Width, (double)frame.Height / image.Height);
        int width = (int)(ratio * image.Width), height = (int)(ratio * image.Height);
        var target = new Rectangle((frame.Width - width) / 2, (frame.Height - height) / 2, width, height);
        using var corners = VesperDrawing.Rounded(target, S(5));
        GraphicsState old = graphics.Save();
        graphics.SetClip(corners, CombineMode.Intersect);
        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        graphics.DrawImage(image, target);
        graphics.Restore(old);
    }

    private void ReplacePoster(Bitmap? image)
    {
        _poster?.Dispose();
        _poster = image;
        _posterFrame.Invalidate();
    }

    private async Task LoadThumbnailAsync(Uri? url)
    {
        if (url is null || url.Scheme != Uri.UriSchemeHttps || IsDisposed || _lifetime.IsCancellationRequested) return;
        if (_posterUrl == url) return;
        _posterLoad?.Cancel();
        _posterLoad?.Dispose();
        _posterLoad = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken token = _posterLoad.Token;
        _posterUrl = url;
        try
        {
            if (!_posterCache.TryGetValue(url, out byte[]? imageBytes))
            {
                imageBytes = await _thumbnails.GetThumbnailAsync(url, token);
                if (imageBytes is null) { if (_posterUrl == url) _posterUrl = null; return; }
                if (token.IsCancellationRequested || IsDisposed) return;
                if (_posterCache.Count >= 8) _posterCache.Remove(_posterCache.Keys.First());
                _posterCache[url] = imageBytes;
            }
            byte[] bytes = imageBytes;
            Bitmap? image = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var ms = new MemoryStream(bytes, writable: false);
                using var decoded = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true);
                if (decoded.Width > 4096 || decoded.Height > 4096 || (long)decoded.Width * decoded.Height > 4_000_000) return null;
                return new Bitmap(decoded);
            }, token);
            if (image is null) return;
            if (token.IsCancellationRequested || IsDisposed) { image.Dispose(); return; }
            ReplacePoster(image);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OutOfMemoryException
            or System.Net.Http.HttpRequestException or Vesper.MediaResolver.Models.MediaResolverException
            or OperationCanceledException or ObjectDisposedException or System.Runtime.InteropServices.ExternalException)
        { if (_posterUrl == url) _posterUrl = null; /* optional artwork; placeholder remains */ }
    }

    private void UpdatePlayableFormats(MediaItem item)
    {
        _playableFormats.Clear();
        var candidates = PlayableOnly(item);
        _playableFormats.AddRange(candidates.Representations
            .Where(r => Prepare(candidates, FormatPreferences.BestQuality, r).Choice is not null)
            .OrderByDescending(r => r.Height ?? 0)
            .ThenByDescending(r => r.FrameRate ?? 0)
            .ThenByDescending(r => r.BitrateKbps ?? 0)
            .ThenBy(r => r.Id, StringComparer.Ordinal));
        if (_playableFormats.Count == 0)
        {
            ShowUnavailable(item.Representations.Count > 0
                ? "Media found, but none of its formats are ready to download."
                : "This page did not provide a downloadable stream.",
                "Try again or open the original page and look for media with the VESPER browser extension.");
            return;
        }

        _item = item with { Representations = _playableFormats.ToArray() };

        string[] options = _playableFormats.Select(r =>
        {
            var parts = new List<string> { r.Content == StreamContent.AudioOnly ? "Audio" : r.Height is { } h ? $"{h}p" : "Video" };
            if (r.AudioUnverified) parts.Add("audio not confirmed");
            if (r.FrameRate is > 30.5) parts.Add($"{Math.Round(r.FrameRate.Value)} fps");
            if ((r.VideoCodec ?? r.AudioCodec) is { Length: > 0 } codec) parts.Add(codec.ToUpperInvariant());
            if (r.Container is { Length: > 0 } container) parts.Add(container.ToUpperInvariant());
            if (r.BitrateKbps is { } bitrate) parts.Add($"{bitrate} kbps");
            return string.Join(" · ", parts);
        }).ToArray();
        _customFormat.SetItems(options);
        MediaDownloadChoice? sourceAudio = Prepare(_item, FormatPreferences.AudioOnly).Choice;
        _canChooseM4a = Prepare(_item, FormatPreferences.AudioOnly, output: MediaOutputPreference.M4a).Choice is { } m4a
            && m4a != sourceAudio;
        _status.Text = $"{_playableFormats.Count} format{(_playableFormats.Count == 1 ? "" : "s")} available.";
        RefreshPresets(_item);
        _notice.Text = "";
        Render(ViewState.Ready);
    }

    private bool AudioOutputApplies(FormatPreferences prefs) =>
        prefs.Mode == QualityMode.AudioOnly || _item?.Representations.All(r => !r.HasVideo) == true;

    private MediaOutputPreference OutputPreference(FormatPreferences prefs)
    {
        if (AudioOutputApplies(prefs))
            return _audioOutputChoice.SelectedIndex == 0 && (_state == ViewState.Collection || _canChooseM4a)
                ? MediaOutputPreference.M4a : MediaOutputPreference.Original;
        return _outputChoice.SelectedIndex == 0 ? MediaOutputPreference.CompatibleVideo : MediaOutputPreference.Original;
    }

    private void RefreshPresets(MediaItem item)
    {
        FormatPreferences[] preferences = [FormatPreferences.BestQuality, FormatPreferences.BalancedQuality,
            FormatPreferences.SmallestSuitable, FormatPreferences.AudioOnly];
        for (int i = 0; i < preferences.Length; i++)
        {
            var result = Prepare(item, preferences[i], output: OutputPreference(preferences[i]));
            if (result.Choice is { } choice)
                _presets[i].SetFormat(choice.DisplayDetail, choice.DisplayContainer, available: true);
            else _presets[i].SetFormat("Unavailable for this source", "", available: false);
        }
    }

    internal void SetPreviewState(int preset, bool pressed = false)
    {
        if (_sample is null) return;
        _presets[preset].Checked = true;
        _presets[preset].Focus();
        _download.PreviewPressed = pressed;
        _download.Invalidate();
    }

    internal void SetPreviewPoster(Bitmap image)
    {
        if (_sample is null) throw new InvalidOperationException("A preview sample is required.");
        ReplacePoster(image);
    }

    private void SelectPreset(int index)
    {
        _presetIndex = index;
        if (_state != ViewState.Ready) return;
        UpdateQualityUi();
        FitToContent();
        if (IsExactFormat && _customFormat.Visible) _body.ScrollControlIntoView(_customFormat);
    }

    private void UpdateQualityUi()
    {
        bool custom = IsExactFormat && _playableFormats.Count > 0;
        _customLabel.Visible = _customFormat.Visible = custom;
        _customFormat.Enabled = custom && !_busy;
        _quality.Enabled = !_busy && _state == ViewState.Collection;
        bool outputVisible = _state == ViewState.Collection || _state == ViewState.Ready && !IsExactFormat;
        bool audio = AudioOutputApplies(CurrentPreferences());
        _outputChoice.Visible = outputVisible && !audio;
        _audioOutputChoice.Visible = outputVisible && audio && (_state == ViewState.Collection || _canChooseM4a);
        UpdateChoiceSummary();
    }

    private void UpdateChoiceSummary()
    {
        if (_state != ViewState.Ready || _item is null || _busy) return;
        FormatPreferences prefs = CurrentPreferences();
        var result = Prepare(_item, prefs, SelectedExactRepresentation(), OutputPreference(prefs));
        if (result.Choice is { } choice)
            _notice.Text = _playableFormats.Any(r => r.Id == choice.FormatId && r.AudioUnverified)
                ? "Audio is unconfirmed and will be checked before publication."
                : IsExactFormat ? choice.DisplayDetail : "";
        else
            _notice.Text = result.Error ?? "This preference isn't available for the source.";
        _notice.ForeColor = result.Choice is null ? _colors.Red : _colors.Muted;
        _notice.Visible = _notice.Text.Length > 0;
        SetButtonsEnabled(result.Choice is not null);
    }

    private MediaRepresentation? SelectedExactRepresentation() =>
        IsExactFormat &&
        _customFormat.SelectedIndex >= 0 && _customFormat.SelectedIndex < _playableFormats.Count
            ? _playableFormats[_customFormat.SelectedIndex] : null;

    private FormatPreferences CurrentPreferences() => (_state == ViewState.Collection ? _quality.SelectedIndex : _presetIndex) switch
    {
        1 => FormatPreferences.BalancedQuality,
        2 => FormatPreferences.SmallestSuitable,
        3 => FormatPreferences.AudioOnly,
        _ => FormatPreferences.BestQuality
    };

    private async Task LoadCollectionAsync()
    {
        if (_item?.Related is not { } hint) return;
        SetBusy(true, "Reading available playlist entries…");
        try
        {
            var result = await _resolver.ResolveAsync(new MediaDiscoveryRequest
            { Url = hint.Url, Options = new DiscoveryOptions { CollectionIntent = CollectionIntent.CollectionOnly, MaxCollectionItems = 200 } }, _lifetime.Token);
            if (!IsDisposed && result.Collection is { } collection) await DisplayCollectionAsync(collection);
            else if (!IsDisposed)
            {
                _status.Text = "This collection couldn't be listed.";
                _notice.Text = result.Error?.UserMessage ?? "Try another supported collection.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!IsDisposed)
            {
                _status.Text = "Playlist discovery failed.";
                _notice.Text = "The original video remains available; try another source.";
            }
        }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private Task DisplayCollectionAsync(MediaCollection collection)
    {
        _quality.SelectedIndex = _presetIndex < 4 ? _presetIndex : 0;
        _item = null;
        _playableFormats.Clear();
        _collection = collection.Entries.Take(200).ToList();
        _truncated = !collection.IsComplete || collection.Entries.Count > 200;
        _title.Text = string.IsNullOrWhiteSpace(collection.Title) ? "Media collection" : collection.Title;
        _source.Text = $"{collection.Provider} · {_collection.Count} items";
        _status.Text = _collection.Count == 0 ? "No accessible entries were listed."
            : "Select the items you want. One quality preference applies across the selection.";
        _entries.Items.Clear();
        foreach (var entry in _collection)
            _entries.Items.Add(Limit(entry.Title ?? entry.MediaId, 105), false);
        _notice.Text = _truncated ? "Showing up to 200 entries; this may be a partial collection snapshot."
            : "Each selected item is checked before entering the download queue.";
        Render(_collection.Count > 0 ? ViewState.Collection : ViewState.Unavailable);
        return Task.CompletedTask;
    }

    private void UpdateCollectionActions()
    {
        if (!IsDisposed && _state == ViewState.Collection) SetButtonsEnabled(_entries.CheckedIndices.Count > 0);
    }

    private async Task MakeSelectionsAsync(bool queueOnly)
    {
        if (_busy) return;
        SetBusy(true, "Checking selected formats…");
        try
        {
            var prefs = CurrentPreferences();
            MediaOutputPreference output = OutputPreference(prefs);
            var jobs = new List<MediaDownloadChoice>();
            var errors = new List<string>();
            if (_collection is null && _item is { } single)
            {
                var prepared = Prepare(single, prefs, SelectedExactRepresentation(), output);
                if (prepared.Choice is { } c) jobs.Add(c);
                else errors.Add(prepared.Error ?? "The chosen format isn't supported.");
            }
            else if (_collection is { } collection)
            {
                int[] selected = _entries.CheckedIndices.Cast<int>().ToArray();
                if (selected.Length == 0) { _status.Text = "Select at least one item."; return; }
                var preparedItems = new (MediaDownloadChoice? Choice, string? Error)[selected.Length];
                int completed = 0;
                var progress = new Progress<int>(count =>
                { if (!IsDisposed) _status.Text = $"Checked {count} of {selected.Length} selected media…"; });
                await Task.Run(() => Parallel.ForEachAsync(Enumerable.Range(0, selected.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = _lifetime.Token }, async (i, token) =>
                {
                    CollectionEntry entry = collection[selected[i]];
                    try
                    {
                        MediaDiscoveryResult result = entry.Resolved is { } ready ? MediaDiscoveryResult.FromItem(ready)
                            : await _resolver.ResolveAsync(new MediaDiscoveryRequest
                            { Url = entry.Url, Options = new DiscoveryOptions { CollectionIntent = CollectionIntent.ItemOnly } }, token);
                        preparedItems[i] = result.Item is { } item ? Prepare(item, prefs, output: output)
                            : (null, result.Error?.UserMessage ?? "Unavailable");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { preparedItems[i] = (null, "This item could not be prepared."); }
                    ((IProgress<int>)progress).Report(Interlocked.Increment(ref completed));
                }), _lifetime.Token);
                for (int i = 0; i < preparedItems.Length; i++)
                {
                    if (preparedItems[i].Choice is { } choice) jobs.Add(choice);
                    else errors.Add($"{Limit(collection[selected[i]].Title ?? collection[selected[i]].MediaId, 35)}: {preparedItems[i].Error}");
                }
            }
            if (IsDisposed || _lifetime.IsCancellationRequested) return;
            if (jobs.Count == 0)
            {
                _status.Text = "No selected media could be prepared.";
                _notice.Text = errors.Count > 0 ? Limit(errors[0], 150) : "Try a different quality or source.";
                return;
            }
            if (errors.Count > 0 && VesperMessageBox.Show(this,
                    $"{jobs.Count} supported item(s); {errors.Count} could not be prepared.\n\n" +
                    string.Join("\n", errors.Take(4)) + "\n\nAdd the supported items?",
                    "Partial media selection", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            // ExpiresAt is retained with each job. Never promise future URL refresh here.
            if (queueOnly && jobs.Any(j => j.ExpiresAt is { } expiry && expiry < DateTimeOffset.UtcNow.AddHours(1)) &&
                VesperMessageBox.Show(this,
                    "One or more media links expire within an hour. Queuing them may cause a 'Needs new link' status later. Queue anyway?",
                    "Temporary media links", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            if (_sample is not null)
            {
                _status.Text = "Preview selection prepared. Sample data stays offline.";
                return;
            }
            Selections = jobs;
            QueueOnly = queueOnly;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!IsDisposed) _status.Text = "The selected media couldn't be prepared."; }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private static string Limit(string text, int length) => text.Length > length ? text[..(length - 1)] + "…" : text;

    private void OpenOriginal()
    {
        try { Process.Start(new ProcessStartInfo(_url.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { VesperMessageBox.Show(this, ex.Message, "Open original", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        if (message is not null) _status.Text = message;
        _retry.Enabled = !_busy;
        _collectionButton.Enabled = !_busy;
        _entries.Enabled = !_busy;
        _quality.Enabled = !_busy && _state == ViewState.Collection;
        _outputChoice.Enabled = !_busy;
        _audioOutputChoice.Enabled = !_busy;
        _presetOptions.Enabled = !_busy;
        _customFormat.Enabled = !_busy && IsExactFormat;
        if (!busy && _state == ViewState.Ready)
        {
            UpdateChoiceSummary();
            FitToContent();
        }
        else SetButtonsEnabled(_state == ViewState.Collection && _entries.CheckedIndices.Count > 0);
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _download.Enabled = _queue.Enabled = enabled && !_busy;
    }
}
