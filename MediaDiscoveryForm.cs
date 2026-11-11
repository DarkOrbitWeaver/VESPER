using System.Diagnostics;
using static VESPER.MediaTransferBridge;
using Vesper.Engine;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Playlists;

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
    private readonly DefaultMediaResolver _resolver = new();
    private readonly SecureHttpClient _thumbnails = new(new ResolverOptions { MaxConcurrentRequests = 1 });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Panel _body;
    private readonly Panel _footer;
    private readonly Panel _posterFrame;
    private readonly PictureBox _poster;
    private readonly Label _title;
    private readonly Label _source;
    private readonly Label _status;
    private readonly Label _notice;
    private readonly Label _qualityLabel;
    private readonly Label _customLabel;
    private readonly VesperChoice _quality;
    private readonly VesperChoice _customFormat;
    private readonly CheckedListBox _entries;
    private readonly VesperButton _download;
    private readonly VesperButton _queue;
    private readonly VesperButton _retry;
    private readonly VesperButton _collectionButton;
    private readonly VesperButton _viewPage;
    private readonly VesperButton _cancel;
    private readonly List<MediaRepresentation> _playableFormats = [];

    private MediaItem? _item;
    private List<CollectionEntry>? _collection;
    private ViewState _state = ViewState.Inspecting;
    private bool _busy;
    private bool _truncated;
    private bool _hasMetadata;
    private CancellationTokenSource? _attempt;
    private CancellationTokenSource? _posterLoad;
    private Uri? _posterUrl;
    private readonly Dictionary<Uri, byte[]> _posterCache = new();

    public IReadOnlyList<MediaDownloadChoice> Selections { get; private set; } = [];
    public bool QueueOnly { get; private set; }

    public MediaDiscoveryForm(VesperPreferences preferences, Uri url)
    {
        _url = url;
        _colors = UiKit.Palette(preferences);
        VesperWindowChrome.Brand(this);
        Text = "Discover media — VESPER";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(650, 366 + VesperDialogChrome.HeaderHeight);
        MinimumSize = new Size(540, 310 + VesperDialogChrome.HeaderHeight);
        Font = new Font("Segoe UI", 9F);
        BackColor = _colors.Surface;
        ForeColor = _colors.Text;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;

        _body = new Panel { Dock = DockStyle.Fill, BackColor = _colors.Surface };
        _footer = new VesperPanel { Dock = DockStyle.Bottom, Height = 62, BackColor = _colors.Surface };
        _footer.Paint += (_, e) =>
        {
            using var line = new Pen(_colors.Border);
            e.Graphics.DrawLine(line, 0, 0, _footer.Width, 0);
        };

        var heading = L("Find media", 17, bold: true);
        heading.SetBounds(24, 19, 560, 34);
        _body.Controls.Add(heading);

        _posterFrame = new VesperPanel { BackColor = _colors.Secondary };
        _posterFrame.Paint += (_, e) =>
        {
            using var pen = new Pen(_colors.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, _posterFrame.Width - 1, _posterFrame.Height - 1);
            if (_poster?.Image is null)
                VesperDrawing.Icon(e.Graphics, VesperIcon.Video,
                    new Rectangle((_posterFrame.Width - 27) / 2, (_posterFrame.Height - 27) / 2, 27, 27), _colors.MutedGreen);
        };
        _poster = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Transparent, SizeMode = PictureBoxSizeMode.Zoom, TabStop = false, Visible = false };
        _posterFrame.Controls.Add(_poster);
        _body.Controls.Add(_posterFrame);

        _title = L("Checking link…", 11, bold: true);
        _title.AutoEllipsis = true;
        _source = L($"{_url.Host} · Media source", 8.5f, muted: true);
        _status = L("Looking for available media. This may take a moment.", 9, muted: true);
        _status.TextAlign = ContentAlignment.TopLeft;
        _status.AutoEllipsis = false;
        _notice = L("", 8.5f, muted: true);
        _notice.TextAlign = ContentAlignment.TopLeft;
        _notice.AutoEllipsis = false;
        _body.Controls.AddRange([_title, _source, _status, _notice]);

        _viewPage = B("View original", 128, symbol: VesperIcon.Link, quiet: true);
        _viewPage.Click += (_, _) => OpenOriginal();
        _body.Controls.Add(_viewPage);

        _qualityLabel = L("Quality", 9, bold: true);
        _quality = new VesperChoice { Width = 370, Height = 34 };
        _quality.ApplyPalette(_colors);
        _quality.SetItems("Best available with audio", "Balanced", "Smallest suitable", "Audio only", "Custom format…");
        _quality.SelectedIndexChanged += (_, _) => UpdateQualityUi();
        _customLabel = L("Exact available format", 8.5f, muted: true);
        _customFormat = new VesperChoice { Width = 572, Height = 34 };
        _customFormat.ApplyPalette(_colors);
        _customFormat.SelectedIndexChanged += (_, _) => UpdateChoiceSummary();
        _body.Controls.AddRange([_qualityLabel, _quality, _customLabel, _customFormat]);

        _collectionButton = B("Browse playlist", 152, VesperIcon.Queue);
        _collectionButton.Click += async (_, _) => await LoadCollectionAsync();
        _body.Controls.Add(_collectionButton);

        _entries = new CheckedListBox
        {
            CheckOnClick = true, BorderStyle = BorderStyle.None,
            BackColor = _colors.Surface, ForeColor = _colors.Text,
            Font = new Font("Segoe UI", 9), IntegralHeight = false, HorizontalScrollbar = true
        };
        _entries.ItemCheck += (_, _) => BeginInvoke(() => UpdateCollectionActions());
        _body.Controls.Add(_entries);

        _cancel = B("Cancel", 88);
        _cancel.DialogResult = DialogResult.Cancel;
        _queue = B("Add to queue", 122, VesperIcon.Queue);
        _download = B("Download", 130, VesperIcon.Download, primary: true);
        _retry = B("Try again", 122, VesperIcon.Refresh);
        _queue.Click += async (_, _) => await MakeSelectionsAsync(queueOnly: true);
        _download.Click += async (_, _) => await MakeSelectionsAsync(queueOnly: false);
        _retry.Click += async (_, _) => await DiscoverAsync();
        _footer.Controls.AddRange([_cancel, _queue, _download, _retry]);

        _body.Resize += (_, _) => LayoutBody();
        _footer.Resize += (_, _) => LayoutFooter();
        Controls.Add(_body);
        Controls.Add(_footer);
        VesperDialogChrome.Attach(this, "VESPER  /  Discover media", _colors, VesperIcon.Video);
        CancelButton = _cancel;
        AcceptButton = _download;
        Render(ViewState.Inspecting);
        Shown += async (_, _) => await DiscoverAsync();
        FormClosing += (_, _) => { _lifetime.Cancel(); _attempt?.Cancel(); _posterLoad?.Cancel(); };
        FormClosed += (_, _) =>
        {
            _poster.Image?.Dispose();
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
        Text = text, AutoSize = false, BackColor = Color.Transparent,
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
        int width = _body.ClientSize.Width;
        if (width < 200) return;
        _posterFrame.SetBounds(24, 68, 126, 79);
        _title.SetBounds(167, 70, Math.Max(80, width - 195), 27);
        _source.SetBounds(167, 99, Math.Max(80, width - 195), 19);
        _viewPage.SetBounds(159, 122, 146, 30);
        _status.SetBounds(24, 167, width - 48, _state is ViewState.Unavailable ? 70 : 40);
        _qualityLabel.SetBounds(24, 207, width - 48, 22);
        _quality.SetBounds(24, 231, width - 48, 34);
        _customLabel.SetBounds(24, 272, width - 48, 20);
        _customFormat.SetBounds(24, 295, width - 48, 34);
        _collectionButton.SetBounds(24, 278, 162, 34);
        _entries.SetBounds(24, 318, width - 48, Math.Max(88, _body.ClientSize.Height - 374));
        _notice.SetBounds(24, _state is ViewState.Collection ? _body.ClientSize.Height - 48 :
            _state is ViewState.Ready ? (_customFormat.Visible ? 339 : 280) : 239,
            width - 48, 43);
    }

    private void LayoutFooter()
    {
        int width = _footer.ClientSize.Width;
        _cancel.SetBounds(24, 13, 88, 35);
        _download.SetBounds(width - 24 - 130, 13, 130, 35);
        _queue.SetBounds(_download.Left - 8 - 122, 13, 122, 35);
        _retry.SetBounds(width - 24 - 122, 13, 122, 35);
    }

    private void Render(ViewState state)
    {
        _state = state;
        bool ready = state is ViewState.Ready;
        bool list = state is ViewState.Collection;
        _qualityLabel.Visible = _quality.Visible = ready || list;
        _collectionButton.Visible = ready && _item?.Related is not null;
        _entries.Visible = list;
        _download.Visible = _queue.Visible = ready || list;
        _retry.Visible = state == ViewState.Unavailable;
        _cancel.Text = state == ViewState.Unavailable ? "Close" : "Cancel";
        _viewPage.Visible = !list;
        UpdateQualityUi();
        // A smaller failure window eliminates the dead, misleading blank area.
        int bodyHeight = list ? 484 : ready ? (_customFormat.Visible ? 383 : 325) : state == ViewState.Unavailable ? 286 : 276;
        int desired = bodyHeight + _footer.Height + VesperDialogChrome.HeaderHeight + 2;
        if (ClientSize.Height != desired) ClientSize = new Size(ClientSize.Width, desired);
        LayoutBody();
        LayoutFooter();
        SetButtonsEnabled(ready && _playableFormats.Count > 0 || list && _entries.CheckedIndices.Count > 0);
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
            {
                _item = item;
                _title.Text = string.IsNullOrWhiteSpace(item.Title) ? "Media source" : item.Title;
                _source.Text = $"{item.Provider}" + (item.Duration is { } d ? $"  ·  {d:hh\\:mm\\:ss}" : "");
                _ = LoadThumbnailAsync(item.Thumbnail);
                UpdatePlayableFormats(item);
            }
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
            _poster.Image?.Dispose();
            _poster.Image = image;
            _poster.Visible = true;
            _posterFrame.Invalidate();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OutOfMemoryException
            or System.Net.Http.HttpRequestException or Vesper.MediaResolver.Models.MediaResolverException
            or OperationCanceledException or ObjectDisposedException or System.Runtime.InteropServices.ExternalException)
        { if (_posterUrl == url) _posterUrl = null; /* optional artwork; placeholder remains */ }
    }

    private void UpdatePlayableFormats(MediaItem item)
    {
        _playableFormats.Clear();
        _playableFormats.AddRange(PlayableOnly(item).Representations
            .OrderByDescending(r => r.Height ?? 0)
            .ThenByDescending(r => r.FrameRate ?? 0)
            .ThenByDescending(r => r.BitrateKbps ?? 0)
            .ThenBy(r => r.Id, StringComparer.Ordinal));
        if (_playableFormats.Count == 0)
        {
            ShowUnavailable(item.Representations.Count > 0
                ? "Media was found, but none of its formats have an implemented complete transfer path."
                : "This page did not provide a playable download stream.",
                "Separate-track muxing, unsupported containers and credential-dependent transfers are unavailable; no silent file will be queued.");
            return;
        }

        string[] options = _playableFormats.Select(r =>
        {
            var parts = new List<string> { r.Content == StreamContent.AudioOnly ? "Audio" : r.Height is { } h ? $"{h}p" : "Video" };
            if (r.AudioUnverified) parts.Add("audio not confirmed");
            if (r.FrameRate is > 30.5) parts.Add($"{Math.Round(r.FrameRate.Value)} fps");
            if (r.VideoCodec is { Length: > 0 } codec) parts.Add(codec.ToUpperInvariant());
            if (r.Container is { Length: > 0 } container) parts.Add(container.ToUpperInvariant());
            if (r.BitrateKbps is { } bitrate) parts.Add($"{bitrate} kbps");
            return string.Join(" · ", parts);
        }).ToArray();
        _customFormat.SetItems(options);
        _status.Text = $"{_playableFormats.Count} compatible format{(_playableFormats.Count == 1 ? "" : "s")} available.";
        _notice.Text = "Only formats VESPER can finish are selectable. Separate streams are not silently queued.";
        Render(ViewState.Ready);
        UpdateChoiceSummary();
    }

    private void UpdateQualityUi()
    {
        bool custom = (_state is ViewState.Ready) && _quality.SelectedIndex == 4 && _playableFormats.Count > 0;
        _customLabel.Visible = _customFormat.Visible = custom;
        _customFormat.Enabled = custom && !_busy;
        _quality.Enabled = !_busy && (_state is ViewState.Ready or ViewState.Collection);
        if (_state == ViewState.Ready && _body is not null && ClientSize.Height > 0)
        {
            int wanted = (custom ? 383 : 325) + _footer.Height + VesperDialogChrome.HeaderHeight + 2;
            if (ClientSize.Height != wanted) ClientSize = new Size(ClientSize.Width, wanted);
        }
        if (_body is not null) LayoutBody();
        UpdateChoiceSummary();
    }

    private void UpdateChoiceSummary()
    {
        if (_state != ViewState.Ready || _item is null || _busy) return;
        FormatPreferences prefs = CurrentPreferences();
        var result = Prepare(_item, prefs, SelectedExactRepresentation());
        if (result.Choice is { } choice)
            _notice.Text = _playableFormats.Any(r => r.Id == choice.FormatId && r.AudioUnverified)
                ? $"Selected: {choice.Quality ?? "Available source"}. Audio is unconfirmed and will be checked before publication."
                : $"Selected: {choice.Quality ?? "Available source"}. Only implemented transfer paths are offered.";
        else
            _notice.Text = result.Error ?? "This preference isn't available for the source.";
        SetButtonsEnabled(result.Choice is not null);
    }

    private MediaRepresentation? SelectedExactRepresentation() =>
        _state == ViewState.Ready && _quality.SelectedIndex == 4 &&
        _customFormat.SelectedIndex >= 0 && _customFormat.SelectedIndex < _playableFormats.Count
            ? _playableFormats[_customFormat.SelectedIndex] : null;

    private FormatPreferences CurrentPreferences() => _quality.SelectedIndex switch
    {
        1 => FormatPreferences.BalancedQuality with { AllowMuxing = false },
        2 => FormatPreferences.SmallestSuitable with { AllowMuxing = false },
        3 => FormatPreferences.AudioOnly with { AllowMuxing = false },
        _ => FormatPreferences.BestQuality with { AllowMuxing = false }
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
            var jobs = new List<MediaDownloadChoice>();
            var errors = new List<string>();
            if (_collection is null && _item is { } single)
            {
                var prepared = Prepare(single, prefs, SelectedExactRepresentation());
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
                        preparedItems[i] = result.Item is { } item ? Prepare(item, prefs)
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
            if (errors.Count > 0 && MessageBox.Show(this,
                    $"{jobs.Count} supported item(s); {errors.Count} could not be prepared.\n\n" +
                    string.Join("\n", errors.Take(4)) + "\n\nAdd the supported items?",
                    "Partial media selection", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            // ExpiresAt is retained with each job. Never promise future URL refresh here.
            if (queueOnly && jobs.Any(j => j.ExpiresAt is { } expiry && expiry < DateTimeOffset.UtcNow.AddHours(1)) &&
                MessageBox.Show(this,
                    "One or more media links expire within an hour. Queuing them may cause a 'Needs new link' status later. Queue anyway?",
                    "Temporary media links", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
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
        { MessageBox.Show(this, ex.Message, "Open original", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        if (message is not null) _status.Text = message;
        _retry.Enabled = !_busy;
        _collectionButton.Enabled = !_busy;
        _entries.Enabled = !_busy;
        _quality.Enabled = !_busy && (_state is ViewState.Ready or ViewState.Collection);
        _customFormat.Enabled = !_busy && _state == ViewState.Ready && _quality.SelectedIndex == 4;
        bool allowed = _state == ViewState.Ready && _playableFormats.Count > 0 ||
            _state == ViewState.Collection && _entries.CheckedIndices.Count > 0;
        SetButtonsEnabled(allowed);
        if (!busy && _state == ViewState.Ready) UpdateChoiceSummary();
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _download.Enabled = _queue.Enabled = enabled && !_busy;
    }
}
