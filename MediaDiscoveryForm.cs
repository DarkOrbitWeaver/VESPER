using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using static VESPER.MediaTransferBridge;
using Vesper.Engine;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Playlists;
using Vesper.MediaResolver.Utilities;
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
    private readonly VesperChoice _audioTrack;
    private readonly VesperButton _subtitleChoice;
    private readonly CheckedListBox _subtitleLanguages;
    private readonly CheckBox _embedSubtitles;
    private readonly ContextMenuStrip _subtitleMenu;
    private readonly ToolStripControlHost _subtitleHost;
    private readonly VesperChoice _customFormat;
    private readonly CheckBox _clipCheck;
    private readonly Label _clipStartLabel;
    private readonly Label _clipEndLabel;
    private readonly TextBox _clipStart;
    private readonly TextBox _clipEnd;
    private readonly VesperRangeBar _clipBar;
    private readonly Label _clipNote;
    private bool _clipRowShown;
    private bool _clipFieldsShown;
    private bool _syncingClip;
    private int _clipExtra;
    private readonly CheckedListBox _entries;
    private readonly VesperButton _download;
    private readonly VesperButton _queue;
    private readonly VesperButton _retry;
    private readonly VesperButton _collectionButton;
    private readonly VesperButton _viewPage;
    private readonly VesperButton _cancel;
    private readonly List<MediaRepresentation> _playableFormats = [];
    private AudioTrack[] _audioTrackOptions = [];
    private SubtitleTrack[] _subtitleTrackOptions = [];
    private readonly Panel _presetOptions;
    private readonly List<MediaQualityOption> _presets = [];
    private readonly MediaItem? _sample;
    private readonly Dictionary<int, string> _collectionAvailability = [];
    private string[] _collectionLabels = [];

    private MediaItem? _item;
    private List<CollectionEntry>? _collection;
    private bool _updatingCollectionRows;
    private ViewState _state = ViewState.Inspecting;
    private bool _busy;
    private bool _truncated;
    private bool _hasMetadata;
    private bool _canChooseM4a;
    private MediaOutputPreference[] _audioOutputOptions = [MediaOutputPreference.M4a, MediaOutputPreference.Original];
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
        if (SiteRule.ForPage(preferences.SiteRules, url.AbsoluteUri)?.AlwaysAudio == true)
            _presetIndex = 3;
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
        _footer = new VesperPanel { Dock = DockStyle.Bottom, Height = S(62), BackColor = _colors.Surface };
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
        _status.AutoEllipsis = true;      // the height is measured below; this is the safety net
        _notice = L("", 8.5f, muted: true);
        _notice.TextAlign = ContentAlignment.TopLeft;
        _notice.AutoEllipsis = true;
        _canvas.Controls.AddRange([_title, _source, _status, _notice]);

        _viewPage = B("View original", 128, symbol: VesperIcon.Link, quiet: true);
        _viewPage.TextAlign = ContentAlignment.MiddleLeft;
        _viewPage.Click += (_, _) => OpenOriginal();
        _canvas.Controls.Add(_viewPage);

        _qualityLabel = L("Quality", 9, bold: true);
        _quality = new VesperChoice
        {
            AccessibleName = "Quality for selected collection items",
            AccessibleDescription = "The same quality preference is matched independently to each checked entry. Entries without a compatible format are skipped.",
            Symbol = VesperIcon.Quality
        };
        _quality.ApplyPalette(_colors);
        _quality.SetItems("Best", "Balanced", "Smallest", "Audio only");
        _outputChoice = new VesperChoice
        {
            AccessibleName = "Video output format",
            AccessibleDescription = "MP4 makes a compatible copy when needed. Original format keeps the selected source format where supported."
        };
        _outputChoice.ApplyPalette(_colors);
        _outputChoice.SetItems("MP4 (compatible)", "Original format");
        _audioOutputChoice = new VesperChoice
        {
            AccessibleName = "Audio output format",
            AccessibleDescription = "M4A makes a compatible copy when needed. Original audio keeps the selected source format where supported."
        };
        _audioOutputChoice.ApplyPalette(_colors);
        _audioOutputChoice.SetItems("M4A (compatible)", "Original audio");
        _audioTrack = new VesperChoice { AccessibleName = "Audio track" };
        _audioTrack.ApplyPalette(_colors);
        _audioTrack.SelectedIndexChanged += (_, _) =>
        {
            if (_state != ViewState.Ready || _item is null) return;
            RefreshPresets(_item);
            UpdateChoiceSummary();
            FitToContent();
        };
        _subtitleChoice = B("Subtitles: Off", 160);
        _subtitleChoice.AccessibleName = "Choose subtitle languages";
        _subtitleChoice.AccessibleDescription = "All subtitle languages are off by default. Choose one or more.";
        _subtitleLanguages = new CheckedListBox
        {
            AccessibleName = "Available subtitle languages",
            AccessibleDescription = "Choose one or more subtitle languages.",
            CheckOnClick = true,
            BorderStyle = BorderStyle.None,
            BackColor = _colors.Surface,
            ForeColor = _colors.Text,
            Font = new Font("Segoe UI", 9),
            IntegralHeight = false,
            HorizontalScrollbar = true
        };
        _subtitleMenu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = false };
        VesperMenuTheme.Apply(_subtitleMenu, _colors);
        _subtitleHost = new ToolStripControlHost(_subtitleLanguages)
        {
            AutoSize = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        _subtitleMenu.Items.Add(_subtitleHost);
        _embedSubtitles = new CheckBox
        {
            Text = "Embed selected subtitles",
            AccessibleName = "Embed selected subtitles",
            AccessibleDescription = "Tries to embed supported SRT or WebVTT subtitles in MP4 or Matroska. Separate subtitle files are still queued; live, segmented, styled, or unsupported subtitles remain separate.",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = _colors.Surface,
            ForeColor = _colors.Text,
            Font = new Font("Segoe UI", 9),
            Visible = false
        };
        _embedSubtitles.CheckedChanged += (_, _) =>
        {
            if (_state == ViewState.Ready && _item is not null)
            {
                RefreshPresets(_item);
                UpdateChoiceSummary();
                FitToContent();
            }
        };
        _subtitleChoice.Click += (_, _) =>
        {
            if (_state == ViewState.Ready && _subtitleTrackOptions.Length > 0)
                _subtitleMenu.Show(_subtitleChoice, 0, _subtitleChoice.Height + 1);
        };
        _subtitleLanguages.ItemCheck += (_, _) =>
        {
            if (!IsDisposed && IsHandleCreated)
                BeginInvoke(() =>
                {
                    UpdateSubtitleSummary();
                    if (_state == ViewState.Ready && _item is not null)
                    {
                        UpdateQualityUi();
                        RefreshPresets(_item);
                        FitToContent();
                    }
                });
        };
        _customLabel = L("Exact available format", 8.5f, muted: true);
        _customFormat = new VesperChoice { AccessibleName = "Exact format", Symbol = VesperIcon.Layers };
        _customFormat.ApplyPalette(_colors);
        _customFormat.SelectedIndexChanged += (_, _) => { UpdateChoiceSummary(); FitToContent(); };
        _canvas.Controls.AddRange([_qualityLabel, _quality, _outputChoice, _audioOutputChoice,
            _audioTrack, _subtitleChoice, _embedSubtitles, _customLabel, _customFormat]);
        _clipCheck = new CheckBox
        {
            Text = "Time range", AccessibleName = "Download only a time range of the media", AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft, BackColor = _colors.Surface, ForeColor = _colors.Text,
            Font = new Font("Segoe UI", 9), Visible = false
        };
        _clipStartLabel = L("From", 8.5f, muted: true);
        _clipEndLabel = L("To", 8.5f, muted: true);
        _clipStart = ClipTimeBox("Clip start time");
        _clipEnd = ClipTimeBox("Clip end time");
        _clipBar = new VesperRangeBar { Visible = false };
        _clipBar.ApplyPalette(_colors);
        _clipNote = L("", 8.5f, muted: true);
        _clipNote.TextAlign = ContentAlignment.TopLeft;
        _clipNote.Visible = false;
        _canvas.Controls.AddRange([_clipCheck, _clipStartLabel, _clipStart, _clipEndLabel, _clipEnd, _clipBar, _clipNote]);
        _clipCheck.CheckedChanged += (_, _) => { UpdateChoiceSummary(); FitToContent(); };
        _clipStart.TextChanged += (_, _) => ClipTextChanged();
        _clipEnd.TextChanged += (_, _) => ClipTextChanged();
        _clipBar.RangeChanged += (_, _) => ClipBarChanged();
        _presetOptions = new Panel { BackColor = _colors.Surface, Visible = false, TabIndex = 3 };
        (string Title, VesperIcon Icon)[] presets =
        [
            ("Best quality", VesperIcon.Crown), ("Balanced", VesperIcon.Quality), ("Smallest", VesperIcon.Leaf),
            ("Audio only", VesperIcon.Audio), ("More formats…", VesperIcon.Layers)
        ];
        for (int i = 0; i < presets.Length; i++)
        {
            var (title, icon) = presets[i];
            var option = new MediaQualityOption(title, icon, _colors) { TabIndex = i };
            _presets.Add(option);
            _presetOptions.Controls.Add(option);
        }
        _presets[_presetIndex].Checked = true;
        _canvas.Controls.Add(_presetOptions);
        void OutputChanged(object? sender, EventArgs e)
        {
            if (_state == ViewState.Collection)
            {
                ClearCollectionAvailability();
                _status.Text = "Output preference changed. Check the selected entries again.";
                _notice.Text = "No entries are added until you confirm; updated Ready or Skipped results will appear in the list.";
                FitToContent();
                return;
            }
            if (_state != ViewState.Ready || _item is null) return;
            RefreshPresets(_item);
            UpdateChoiceSummary();
            FitToContent();
        }
        _outputChoice.SelectedIndexChanged += OutputChanged;
        _audioOutputChoice.SelectedIndexChanged += OutputChanged;
        _quality.SelectedIndexChanged += (_, _) =>
        {
            if (_state != ViewState.Collection) return;
            ClearCollectionAvailability();
            _status.Text = "Check the selected entries to see which formats are available.";
            _notice.Text = "The quality preference is applied separately to checked entries. Nothing is added until you confirm.";
            UpdateQualityUi();
            FitToContent();
        };

        _collectionButton = B("Browse playlist", 152, VesperIcon.Queue);
        _collectionButton.AccessibleName = "Browse playlist items";
        _collectionButton.Click += async (_, _) => await LoadCollectionAsync();
        _canvas.Controls.Add(_collectionButton);

        _entries = new VesperCheckedList(_colors)
        {
            CheckOnClick = true, BorderStyle = BorderStyle.None,
            BackColor = _colors.Surface, ForeColor = _colors.Text,
            Font = new Font("Segoe UI", 9), IntegralHeight = false, HorizontalScrollbar = true
        };
        _entries.ItemCheck += (_, _) =>
        {
            if (_updatingCollectionRows) return;
            BeginInvoke(() =>
            {
                if (_state == ViewState.Collection)
                {
                    ClearCollectionAvailability();
                    _status.Text = "Selection changed. Check the selected entries again.";
                    _notice.Text = "No entries are added until you confirm; updated Ready or Skipped results will appear in the list.";
                    UpdateCollectionActions();
                    FitToContent();
                }
            });
        };
        _canvas.Controls.Add(_entries);

        _cancel = B("Cancel", 88);
        _cancel.DialogResult = DialogResult.Cancel;
        _queue = B("Add to queue", 122, VesperIcon.Queue);
        _download = B("Download", 130, VesperIcon.Download, primary: true);
        _retry = B("Check again", 122, VesperIcon.Refresh);
        _retry.AccessibleName = "Check available media again";
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
            _subtitleMenu.Dispose();
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
        Font = UiKit.Text(size, bold ? FontStyle.Bold : FontStyle.Regular),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private VesperButton B(string text, int width, VesperIcon symbol = VesperIcon.None, bool primary = false, bool quiet = false)
    {
        var button = new VesperButton { Text = text, Width = width, Height = 35, Primary = primary, Quiet = quiet, Symbol = symbol };
        button.ApplyPalette(_colors);
        return button;
    }

    // Text blocks get the height their words need (up to four lines) instead of a fixed box; everything
    // below them moves down by the same amount and the dialog grows to match.
    private int _statusExtra, _noticeExtra;

    private int ExtraLines(Label label, int widthPx, int fixedLogicalHeight)
    {
        if (string.IsNullOrEmpty(label.Text) || widthPx <= 0) return 0;
        Size need = TextRenderer.MeasureText(label.Text, label.Font, new Size(widthPx, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        int capped = Math.Min(need.Height, label.Font.Height * 4);
        int neededLogical = (int)Math.Ceiling(capped * 96d / DeviceDpi) + 4;
        return Math.Max(0, neededLogical - fixedLogicalHeight);
    }

    // The status label ends above the track and output controls.
    private int StatusBaseHeight => _state == ViewState.Unavailable ? 57 : 28;

    // Height of the time bar and the time-range message below the Time range row, in logical pixels.
    private int ClipExtraHeight(int contentWidth)
    {
        if (_state != ViewState.Ready || !_clipRowShown) return 0;
        int extra = _clipFieldsShown ? 28 : 0;
        if (_clipNote.Text.Length > 0) extra += 20 + ExtraLines(_clipNote, contentWidth, 18);
        return extra;
    }

    private void LayoutBody()
    {
        if (_arranging || _body.ClientSize.Width < S(150)) return;
        _arranging = true;
        try
        {
            int width = _canvas.ClientSize.Width;
            int margin = S(24), contentWidth = Math.Max(1, width - 2 * margin);
            bool hasPlaylist = _state == ViewState.Ready && _item?.Related is not null;
            _statusExtra = ExtraLines(_status, contentWidth - (hasPlaylist ? S(172) : 0), StatusBaseHeight);
            _clipExtra = ClipExtraHeight(contentWidth);
            _noticeExtra = ExtraLines(_notice, contentWidth, 40);
            _canvas.Height = S(BodyHeight());
            width = _canvas.ClientSize.Width;
            contentWidth = Math.Max(1, width - 2 * margin);
            int shift = 0;
            void Place(Control control, int x, int y, int w, int h) =>
                control.SetBounds(x, S(y + shift), Math.Max(1, w), S(h));

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
            Place(_status, margin, 153, statusWidth, StatusBaseHeight + _statusExtra);
            if (_state == ViewState.Ready) _status.Height = S(181 + _statusExtra) - _status.Top;
            Place(_collectionButton, width - margin - S(162), 143, S(162), 34);
            shift = _statusExtra;                       // everything below the status text moves down with it
            int outputWidth = Math.Min(S(132), contentWidth / 2);
            int subtitleShift = _embedSubtitles.Visible ? 29 : 0;
            Place(_outputChoice, width - margin - outputWidth, 181, outputWidth, 28);
            Place(_audioOutputChoice, width - margin - outputWidth, 181, outputWidth, 28);
            if (_audioTrack.Visible && _subtitleChoice.Visible)
            {
                int trackAreaWidth = contentWidth - (_outputChoice.Visible || _audioOutputChoice.Visible ? outputWidth + S(8) : 0);
                int choiceWidth = (trackAreaWidth - S(8)) / 2;
                Place(_audioTrack, margin, 181, choiceWidth, 28);
                Place(_subtitleChoice, margin + choiceWidth + S(8), 181, choiceWidth, 28);
            }
            else if (_audioTrack.Visible)
            {
                int trackAreaWidth = contentWidth - (_outputChoice.Visible || _audioOutputChoice.Visible ? outputWidth + S(8) : 0);
                Place(_qualityLabel, margin, 181, S(96), 26);
                Place(_audioTrack, margin + S(96), 181, trackAreaWidth - S(96), 28);
            }
            else if (_subtitleChoice.Visible)
            {
                int trackAreaWidth = contentWidth - (_outputChoice.Visible || _audioOutputChoice.Visible ? outputWidth + S(8) : 0);
                Place(_subtitleChoice, margin, 181, trackAreaWidth, 28);
            }
            else
                Place(_qualityLabel, margin, 181, contentWidth - outputWidth - S(8), 26);
            Place(_embedSubtitles, margin, 211, contentWidth, 25);
            Place(_quality, margin, 211 + subtitleShift, contentWidth, 34);
            Place(_entries, margin, 256 + subtitleShift, contentWidth, 152);
            Place(_presetOptions, margin, 211 + subtitleShift, contentWidth, 181);
            for (int i = 0; i < _presets.Count; i++)
                _presets[i].SetBounds(0, S(i * 37), contentWidth, S(i < 4 ? 37 : 33));
            Place(_customLabel, margin, 398 + subtitleShift, contentWidth, 18);
            Place(_customFormat, margin, 419 + subtitleShift, contentWidth, 34);
            int clipY = (IsExactFormat ? 457 : 402) + subtitleShift;
            Place(_clipCheck, margin, clipY, S(126), 29);
            Place(_clipStartLabel, margin + S(134), clipY, S(40), 29);
            Place(_clipStart, margin + S(174), clipY + 2, S(86), 25);
            Place(_clipEndLabel, margin + S(272), clipY, S(26), 29);
            Place(_clipEnd, margin + S(300), clipY + 2, S(86), 25);
            int clipNoteY = clipY + 31;
            if (_clipFieldsShown)
            {
                Place(_clipBar, margin, clipNoteY, contentWidth, 26);
                clipNoteY += 28;
            }
            Place(_clipNote, margin, clipNoteY, contentWidth, Math.Max(18, _clipExtra - (_clipFieldsShown ? 28 : 0) - 2));
            int noticeY = _state switch
            {
                ViewState.Ready => (IsExactFormat ? (_clipRowShown ? 495 : 464) : (_clipRowShown ? 440 : 402)) + _clipExtra,
                ViewState.Collection => 417,
                ViewState.Unavailable => 213,
                _ => 191
            };
            if (_state == ViewState.Ready) noticeY += subtitleShift;
            Place(_notice, margin, noticeY, contentWidth, 40 + _noticeExtra);
        }
        finally { _arranging = false; }
    }

    private int S(int logicalPixels) => UiKit.Scale(this, logicalPixels);

    private void LayoutFooter()
    {
        int width = _footer.ClientSize.Width;
        int height = S(35);
        if (width >= S(380))
        {
            int margin = S(width < S(430) ? 12 : 24), y = S(13);
            _cancel.SetBounds(margin, y, S(88), height);
            _download.SetBounds(width - margin - S(130), y, S(130), height);
            _queue.SetBounds(_download.Left - S(8 + 122), y, S(122), height);
            _retry.SetBounds(width - margin - S(122), y, S(122), height);
            return;
        }

        int narrowMargin = S(12), top = S(8), gap = S(6);
        _cancel.SetBounds(narrowMargin, top, S(88), height);
        if (width >= S(242))
        {
            int right = width - narrowMargin;
            _queue.SetBounds(_cancel.Right + S(8), top, S(122), height);
            _retry.SetBounds(right - S(122), top, S(122), height);
            _download.SetBounds(right - S(130), top + height + gap, S(130), height);
            return;
        }

        int secondRow = top + height + gap;
        int thirdRow = secondRow + height + gap;
        _queue.SetBounds(width - narrowMargin - S(122), secondRow, S(122), height);
        _retry.SetBounds(width - narrowMargin - S(122), secondRow, S(122), height);
        _download.SetBounds(width - narrowMargin - S(130), thirdRow, S(130), height);
    }

    private bool IsExactFormat => _state == ViewState.Ready && _presetIndex == 4;

    private int BodyHeight() => _statusExtra + _noticeExtra + _state switch
    {
        ViewState.Ready => (IsExactFormat ? (_clipRowShown ? 541 : 510)
            : _clipRowShown ? 485 : string.IsNullOrEmpty(_notice.Text) ? 407 : 448)
            + (_embedSubtitles.Visible ? 29 : 0) + _clipExtra,
        ViewState.Collection => 464,
        ViewState.Unavailable => 260,
        _ => 214
    };

    private void FitToContent()
    {
        Rectangle work = Screen.FromControl(Visible ? this : Owner ?? this).WorkingArea;
        int inset = S(12);
        int width = Math.Min(ClientSize.Width, Math.Max(1, work.Width - 2 * inset));
        int footerWidth = Math.Max(1, width - 2 * S(1));
        int footerHeight = S(62);
        if (footerWidth < S(380))
        {
            footerHeight = S(133);
            if (footerWidth >= S(242)) footerHeight = S(92);
        }
        if (_footer.Height != footerHeight) _footer.Height = footerHeight;
        int desiredHeight = S(BodyHeight()) + _footer.Height + VesperDialogChrome.VerticalInset(this);
        int height = Math.Min(desiredHeight, Math.Max(1, work.Height - 2 * inset));
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
        if (!ready) SetClipControls(show: false, available: false);
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
                    ? "Open the page in your browser and check the VESPER picker while the media is playing."
                    : "This page may not expose a downloadable or supported stream.";
                ShowUnavailable(message, hint);

            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (!_lifetime.IsCancellationRequested)
        {
            if (!IsDisposed) ShowUnavailable("This media source could not be inspected.",
                "Check your connection, then check again or use the VESPER browser picker.");
        }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private void DisplayItem(MediaItem item)
    {
        _item = item;
        _clipCheck.Checked = false;
        _syncingClip = true;
        _clipStart.Text = "00:00";
        _clipEnd.Text = item.Duration is { } known ? ClipSelection.TimeText(known) : "";
        _syncingClip = false;
        double length = item.Duration is { TotalSeconds: > 0 and <= ClipSelection.LongestSeconds } whole ? whole.TotalSeconds : 0;
        _clipBar.SetDuration(length);
        _clipBar.SetRange(0, length);
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
            _title.Text = "Could not check this page";
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
        ConfigureAudioTracks(_item);
        ConfigureSubtitleTracks(_item);

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
        var audioOutputs = new List<(MediaOutputPreference Output, string Label)>();
        if (Prepare(_item, FormatPreferences.AudioOnly, output: MediaOutputPreference.M4a).Choice is { DisplayContainer: "M4A" })
            audioOutputs.Add((MediaOutputPreference.M4a, "M4A (compatible)"));
        audioOutputs.Add((MediaOutputPreference.Original, "Original audio"));
        if (Prepare(_item, FormatPreferences.AudioOnly, output: MediaOutputPreference.Opus).Choice is not null)
            audioOutputs.Add((MediaOutputPreference.Opus, "Opus — copied, no re-encoding"));
        if (Prepare(_item, FormatPreferences.AudioOnly, output: MediaOutputPreference.Wav).Choice is not null)
            audioOutputs.Add((MediaOutputPreference.Wav, "WAV — decoded PCM"));
        if (Prepare(_item, FormatPreferences.AudioOnly, output: MediaOutputPreference.Mp3).Choice is not null)
            audioOutputs.Add((MediaOutputPreference.Mp3, "MP3 — re-encode when needed"));
        _audioOutputOptions = audioOutputs.Select(o => o.Output).ToArray();
        _audioOutputChoice.SetItems(audioOutputs.Select(o => o.Label).ToArray());
        _canChooseM4a = audioOutputs.Count > 1;
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
            return _audioOutputOptions.ElementAtOrDefault(Math.Max(0, _audioOutputChoice.SelectedIndex));
        return _outputChoice.SelectedIndex == 0 ? MediaOutputPreference.CompatibleVideo : MediaOutputPreference.Original;
    }

    private void RefreshPresets(MediaItem item)
    {
        FormatPreferences[] preferences = [FormatPreferences.BestQuality, FormatPreferences.BalancedQuality,
            FormatPreferences.SmallestSuitable, FormatPreferences.AudioOnly];
        for (int i = 0; i < preferences.Length; i++)
        {
            FormatPreferences selected = WithSelectedTracks(preferences[i]);
            var result = Prepare(item, selected, output: OutputPreference(selected));
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
        bool hasTrackChoice = _state == ViewState.Ready && _audioTrackOptions.Length > 1;
        bool hasSubtitleChoice = _state == ViewState.Ready && _subtitleTrackOptions.Length > 0;
        bool hasSelectedSubtitle = _state == ViewState.Ready && _subtitleLanguages.CheckedIndices.Count > 0;
        _audioTrack.Visible = hasTrackChoice;
        _subtitleChoice.Visible = hasSubtitleChoice;
        if (!hasSelectedSubtitle) _embedSubtitles.Checked = false;
        _embedSubtitles.Visible = hasSelectedSubtitle;
        _embedSubtitles.Enabled = !_busy;
        _qualityLabel.Visible = (_state == ViewState.Ready || _state == ViewState.Collection) && !hasSubtitleChoice;
        _qualityLabel.Text = _state == ViewState.Collection ? "Quality for selected items"
            : hasTrackChoice ? "Audio track" : "Quality";
        _customLabel.Visible = _customFormat.Visible = custom;
        _customFormat.Enabled = custom && !_busy;
        _quality.Enabled = !_busy && _state == ViewState.Collection;
        bool outputVisible = _state == ViewState.Collection || _state == ViewState.Ready && !IsExactFormat;
        bool audio = AudioOutputApplies(CurrentPreferences());
        _outputChoice.Visible = outputVisible && !audio;
        _audioOutputChoice.Visible = outputVisible && audio && (_state == ViewState.Collection || _canChooseM4a);
        UpdateChoiceSummary();
    }

    private const string ClipNotPossible =
        "This format can't be cut to a time range. Turn off Time range to download all of it, or pick another format.";

    private void UpdateChoiceSummary()
    {
        if (_state != ViewState.Ready || _item is null || _busy) return;
        FormatPreferences prefs = CurrentPreferences();
        var complete = Prepare(_item, prefs, SelectedExactRepresentation(), OutputPreference(prefs));
        ClipMode mode = complete.Choice is { } fullChoice ? ClipSelection.ModeFor(fullChoice) : ClipMode.None;
        string? unavailable = ClipSelection.UnavailableReason(_item.IsLive, _item.Duration, mode);
        bool wantsClip = _clipCheck.Checked;
        // No format, no time-range row: the format problem is the message that matters.
        SetClipControls(show: complete.Choice is not null, available: unavailable is null);

        // A chosen range is never dropped quietly. If the new format cannot be cut, the buttons stay off
        // until the user turns Time range off or picks a format that can.
        var result = complete;
        string clipError = "";
        string clipText = unavailable ?? "";
        if (wantsClip && complete.Choice is not null)
        {
            if (unavailable is not null) clipError = ClipNotPossible;
            else if (TryGetClipRange(out double start, out double end, out clipError))
            {
                result = Prepare(_item, prefs, SelectedExactRepresentation(), OutputPreference(prefs), start, end);
                if (result.Choice is null) clipError = result.Error ?? "This time range isn't available for the source.";
                else clipText = ClipRangeSummary(start, end, _item.Duration!.Value.TotalSeconds) + ". " + ClipSelection.Notice(mode);
            }
            if (clipError.Length > 0) clipText = clipError;
        }
        _clipNote.Text = complete.Choice is null ? "" : clipText;
        _clipNote.ForeColor = clipError.Length > 0 ? _colors.Red : _colors.Muted;
        _clipNote.Visible = _clipRowShown && _clipNote.Text.Length > 0;

        // The format line is separate from the time-range line above, so neither hides the other.
        if ((result.Choice ?? complete.Choice) is { } choice)
        {
            _notice.Text = _playableFormats.Any(r => r.Id == choice.FormatId && r.AudioUnverified)
                ? "Audio is unconfirmed and will be checked before publication."
                : IsExactFormat ? choice.DisplayDetail : "";
            if (choice.SubtitleTracks is { Count: > 0 })
            {
                string subtitleNotice = choice.SubtitleNotice ?? "Selected subtitles are saved as separate files.";
                _notice.Text = (_notice.Text.Length > 0 ? _notice.Text + " " : "") + subtitleNotice;
            }
        }
        else
            _notice.Text = complete.Error ?? "This preference isn't available for the source.";
        _notice.ForeColor = complete.Choice is null ? _colors.Red : _colors.Muted;
        _notice.Visible = _notice.Text.Length > 0;
        SetButtonsEnabled(result.Choice is not null && clipError.Length == 0);

        int contentWidth = Math.Max(1, _canvas.ClientSize.Width - 2 * S(24));
        if (ClipExtraHeight(contentWidth) != _clipExtra) FitToContent();
    }

    private (MediaDownloadChoice? Choice, string? Error) PrepareCurrentChoice()
    {
        if (_item is null) return (null, "The selected media is unavailable.");
        FormatPreferences prefs = CurrentPreferences();
        var complete = Prepare(_item, prefs, SelectedExactRepresentation(), OutputPreference(prefs));
        if (!_clipCheck.Checked || complete.Choice is null) return complete;
        // Checked again at submission, so a hidden or stale range can never turn into a full download.
        if (ClipSelection.UnavailableReason(_item.IsLive, _item.Duration, ClipSelection.ModeFor(complete.Choice)) is not null)
            return (null, ClipNotPossible);
        if (!TryGetClipRange(out double start, out double end, out string error)) return (null, error);
        return Prepare(_item, prefs, SelectedExactRepresentation(), OutputPreference(prefs), start, end);
    }

    private bool TryGetClipRange(out double start, out double end, out string error) =>
        ClipSelection.TryGetRange(_clipStart.Text, _clipEnd.Text, _item?.Duration, out start, out end, out error);

    // Typed times move the bar; a dragged handle rewrites the typed times. _syncingClip stops the echo.
    private void ClipTextChanged()
    {
        if (_syncingClip) return;
        if (TryGetClipRange(out double start, out double end, out _)) _clipBar.SetRange(start, end);
        UpdateChoiceSummary();
    }

    private void ClipBarChanged()
    {
        _syncingClip = true;
        try
        {
            _clipStart.Text = ClipSelection.TimeText(_clipBar.StartSeconds);
            _clipEnd.Text = ClipSelection.TimeText(_clipBar.EndSeconds);
        }
        finally { _syncingClip = false; }
        UpdateChoiceSummary();
    }

    private TextBox ClipTimeBox(string accessibleName) => new()
    {
        AccessibleName = accessibleName, BorderStyle = BorderStyle.FixedSingle,
        BackColor = _colors.Surface, ForeColor = _colors.Text, Font = new Font("Segoe UI", 9),
        TextAlign = HorizontalAlignment.Center, Visible = false
    };

    // show: the Time range row belongs on screen. available: the chosen format can be cut.
    // A checked box stays usable when the format cannot be cut, so the user can turn it off.
    private void SetClipControls(bool show, bool available)
    {
        _clipRowShown = show;
        _clipFieldsShown = show && available && _clipCheck.Checked;
        _clipCheck.Visible = show;
        _clipStart.Visible = _clipEnd.Visible = _clipStartLabel.Visible = _clipEndLabel.Visible = _clipFieldsShown;
        _clipBar.Visible = _clipFieldsShown;
        if (!show) _clipNote.Visible = false;
        _clipCheck.Enabled = !_busy && (available || _clipCheck.Checked);
        _clipStart.Enabled = _clipEnd.Enabled = _clipBar.Enabled = !_busy;
    }

    private MediaRepresentation? SelectedExactRepresentation() =>
        IsExactFormat &&
        _customFormat.SelectedIndex >= 0 && _customFormat.SelectedIndex < _playableFormats.Count
            ? _playableFormats[_customFormat.SelectedIndex] : null;

    private FormatPreferences CurrentPreferences() => (_state == ViewState.Collection ? _quality.SelectedIndex : _presetIndex) switch
    {
        1 => WithSelectedTracks(FormatPreferences.BalancedQuality),
        2 => WithSelectedTracks(FormatPreferences.SmallestSuitable),
        3 => WithSelectedTracks(FormatPreferences.AudioOnly),
        _ => WithSelectedTracks(FormatPreferences.BestQuality)
    };

    private FormatPreferences WithSelectedTracks(FormatPreferences preferences)
    {
        string? trackId = _state == ViewState.Ready && _audioTrack.Visible && _audioTrack.SelectedIndex > 0
            && _audioTrack.SelectedIndex <= _audioTrackOptions.Length
                ? _audioTrackOptions[_audioTrack.SelectedIndex - 1].Id : null;
        FormatPreferences subtitlePreferences = WithSubtitleSelection(preferences, _subtitleTrackOptions,
            _state == ViewState.Ready ? _subtitleLanguages.CheckedIndices.Cast<int>() : []);
        return subtitlePreferences with
        {
            AudioTrackId = trackId,
            EmbedSubtitles = _state == ViewState.Ready && _embedSubtitles.Checked
        };
    }

    internal static FormatPreferences WithSubtitleSelection(FormatPreferences preferences,
        IReadOnlyList<SubtitleTrack> options, IEnumerable<int> selectedIndices)
    {
        SubtitleTrack[] selected = selectedIndices.Distinct()
            .Where(index => index >= 0 && index < options.Count)
            .Select(index => options[index])
            .ToArray();
        return preferences with
        {
            SubtitleLanguages = selected.Select(subtitle => TextUtil.NormalizeLang(subtitle.Language))
                .Where(language => language.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            IncludeAutoSubtitles = selected.Any(subtitle => subtitle.IsAutoGenerated)
        };
    }

    private void ConfigureAudioTracks(MediaItem item)
    {
        var availableIds = item.Representations
            .Where(representation => representation.HasAudio && !string.IsNullOrWhiteSpace(representation.AudioTrackId))
            .Select(representation => representation.AudioTrackId!)
            .ToHashSet(StringComparer.Ordinal);
        AudioTrack[] inferred = item.Representations
            .Where(representation => representation.HasAudio && !string.IsNullOrWhiteSpace(representation.AudioTrackId))
            .Select(representation => new AudioTrack(representation.AudioTrackId!, representation.AudioLanguage, null,
                representation.IsDefaultAudio, representation.AudioChannels, representation.AudioCodec)).ToArray();
        _audioTrackOptions = item.AudioTracks.Concat(inferred)
            .Where(track => availableIds.Contains(track.Id))
            .GroupBy(track => track.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(track => track.IsDefault)
            .ThenBy(track => track.Language, StringComparer.OrdinalIgnoreCase)
            .ThenBy(track => track.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(track => track.Id, StringComparer.Ordinal)
            .ToArray();
        string[] labels = AudioTrackLabels(_audioTrackOptions);
        var duplicateLabels = labels.GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < labels.Length; i++)
            if (duplicateLabels.Contains(labels[i])) labels[i] += $" · Track {i + 1}";
        _audioTrack.SetItems(["Automatic", .. labels]);
    }

    private void ConfigureSubtitleTracks(MediaItem item)
    {
        _subtitleTrackOptions = MediaTransferBridge.SelectableSubtitleTracks(item.Subtitles);
        _subtitleLanguages.BeginUpdate();
        _subtitleLanguages.Items.Clear();
        foreach (SubtitleTrack subtitle in _subtitleTrackOptions)
            _subtitleLanguages.Items.Add(SubtitleTrackLabel(subtitle));
        _subtitleLanguages.EndUpdate();
        int rows = Math.Max(1, Math.Min(7, _subtitleTrackOptions.Length));
        int longest = 0;
        foreach (object entry in _subtitleLanguages.Items)
            longest = Math.Max(longest, TextRenderer.MeasureText(entry.ToString(), _subtitleLanguages.Font, Size.Empty,
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width);
        int screenWidth = Screen.FromControl(this).WorkingArea.Width - S(40);
        int width = Math.Min(screenWidth, Math.Max(Math.Max(S(220), _subtitleChoice.Width), longest + S(56)));
        _subtitleLanguages.Size = new Size(width, S(rows * 25 + 6));
        _subtitleHost.Size = _subtitleLanguages.Size;
        UpdateSubtitleSummary();
    }

    // Only the facts that actually differ between the tracks go into the labels. Five English AAC stereo
    // tracks used to read "Audio: English · en · 2 ch · AAC · default" five times and get cut off.
    private static string[] AudioTrackLabels(AudioTrack[] tracks)
    {
        bool differentChannels = tracks.Select(t => t.Channels).Distinct().Count() > 1;
        bool differentCodecs = tracks.Select(t => t.Codec?.ToLowerInvariant()).Distinct().Count() > 1;
        var labels = new string[tracks.Length];
        for (int i = 0; i < tracks.Length; i++)
        {
            AudioTrack track = tracks[i];
            string name = TextUtil.Clip(track.Name ?? "", 60);
            string language = track.Language is { Length: > 0 } code ? TextUtil.NormalizeLang(code) : "";
            string label = name.Length > 0 ? name : language.Length > 0 ? language : $"Track {i + 1}";
            var details = new List<string>();
            if (language.Length > 0 && !string.Equals(label, language, StringComparison.OrdinalIgnoreCase)
                && !label.Contains(language, StringComparison.OrdinalIgnoreCase)) details.Add(language);
            if (differentChannels && track.Channels is { } channels) details.Add(channels == 1 ? "mono" : channels == 2 ? "stereo" : $"{channels} channels");
            if (differentCodecs && track.Codec is { Length: > 0 } codec) details.Add(codec.ToUpperInvariant());
            if (track.IsDefault) details.Add("original");
            labels[i] = details.Count == 0 ? label : label + " · " + string.Join(" · ", details);
        }
        // Still identical? Number them rather than show the same words twice.
        for (int i = 0; i < labels.Length; i++)
        {
            int same = 0;
            for (int j = 0; j < labels.Length; j++) if (labels[j] == labels[i]) same++;
            if (same > 1)
            {
                int n = 0;
                string text = labels[i];
                for (int j = 0; j < labels.Length; j++) if (labels[j] == text) labels[j] = $"{text} ({++n})";
            }
        }
        return labels;
    }

    private static string SubtitleTrackLabel(SubtitleTrack track)
    {
        string language = TextUtil.NormalizeLang(track.Language);
        string name = TextUtil.Clip(track.Name, 80);
        string label = name.Length > 0 && !string.Equals(name, language, StringComparison.OrdinalIgnoreCase)
            ? language + " · " + name : language;
        if (track.IsAutoGenerated) label += " · automatic";
        return label;
    }

    private void UpdateSubtitleSummary()
    {
        int count = _subtitleLanguages.CheckedIndices.Count;
        if (count == 0)
        {
            _subtitleChoice.Text = "Subtitles: Off";
            return;
        }
        if (count > 1)
        {
            _subtitleChoice.Text = $"Subtitles: {count} languages";
            return;
        }
        int index = _subtitleLanguages.CheckedIndices[0];
        string language = index < _subtitleTrackOptions.Length
            ? TextUtil.Clip(_subtitleTrackOptions[index].Name, 24) : "1 language";
        if (language.Length == 0 && index < _subtitleTrackOptions.Length)
            language = TextUtil.NormalizeLang(_subtitleTrackOptions[index].Language);
        _subtitleChoice.Text = "Subtitles: " + language;
    }

    private async Task LoadCollectionAsync()
    {
        if (_item?.Related is not { } hint) return;
        _collectionButton.Text = "Reading…";
        _collectionButton.Symbol = VesperIcon.Refresh;
        SetBusy(true, "Reading available playlist entries…");
        try
        {
            var result = await _resolver.ResolveAsync(new MediaDiscoveryRequest
            { Url = hint.Url, Options = new DiscoveryOptions { CollectionIntent = CollectionIntent.CollectionOnly, MaxCollectionItems = 200 } }, _lifetime.Token);
            if (!IsDisposed && result.Collection is { } collection) await DisplayCollectionAsync(collection);
            else if (!IsDisposed)
            {
                _status.Text = "Couldn't list these items.";
                _notice.Text = result.Error?.UserMessage ?? "Check again or open the original page.";
                SetCollectionRetryLabel();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!IsDisposed)
            {
                _status.Text = "Couldn't read these items.";
                _notice.Text = "Check your connection, then try listing them again.";
                SetCollectionRetryLabel();
            }
        }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private void SetCollectionRetryLabel()
    {
        _collectionButton.Text = "Try again";
        _collectionButton.Symbol = VesperIcon.Refresh;
        _collectionButton.AccessibleName = "Try listing these items again";
    }

    private Task DisplayCollectionAsync(MediaCollection collection)
    {
        _quality.SelectedIndex = _presetIndex < 4 ? _presetIndex : 0;
        _item = null;
        _playableFormats.Clear();
        _collection = collection.Entries.Take(200).ToList();
        _truncated = !collection.IsComplete || collection.Entries.Count > 200;
        _collectionLabels = _collection.Select(entry => Limit(entry.Title ?? entry.MediaId, 105)).ToArray();
        _collectionAvailability.Clear();
        _title.Text = string.IsNullOrWhiteSpace(collection.Title) ? "Media collection" : collection.Title;
        _source.Text = $"{collection.Provider} · {_collection.Count} items";
        _status.Text = _collection.Count == 0 ? "No accessible entries were listed."
            : "Choose entries and one quality. VESPER finds each selected entry's best matching format.";
        _entries.Items.Clear();
        foreach (string label in _collectionLabels)
            _entries.Items.Add(label, false);
        _notice.Text = _truncated ? "Showing up to 200 entries; this may be a partial collection snapshot."
            : "Only checked entries are prepared. Incompatible entries are labeled and skipped unless you confirm the available items.";
        Render(_collection.Count > 0 ? ViewState.Collection : ViewState.Unavailable);
        return Task.CompletedTask;
    }

    private void SetCollectionAvailability(int[] selected, (MediaDownloadChoice? Choice, string? Error)[] prepared)
    {
        if (_collection is null) return;
        for (int i = 0; i < selected.Length; i++)
        {
            var result = prepared[i];
            _collectionAvailability[selected[i]] = CollectionAvailabilityText(result.Choice, result.Error);
        }
        RefreshCollectionRows();
    }

    private void ClearCollectionAvailability()
    {
        _collectionAvailability.Clear();
        RefreshCollectionRows();
    }

    private void RefreshCollectionRows()
    {
        if (_collection is null || _entries.Items.Count != _collectionLabels.Length) return;
        bool[] checkedItems = Enumerable.Range(0, _entries.Items.Count).Select(_entries.GetItemChecked).ToArray();
        _updatingCollectionRows = true;
        _entries.BeginUpdate();
        try
        {
            for (int i = 0; i < _collectionLabels.Length; i++)
            {
                string label = _collectionAvailability.TryGetValue(i, out string? status)
                    ? CollectionRowText(_collectionLabels[i], status) : _collectionLabels[i];
                _entries.Items[i] = label;
            }
            for (int i = 0; i < checkedItems.Length; i++) _entries.SetItemChecked(i, checkedItems[i]);
        }
        finally
        {
            _entries.EndUpdate();
            _updatingCollectionRows = false;
        }
    }

    internal static (MediaDownloadChoice? Choice, string? Error) PrepareCollectionEntry(MediaItem item,
        FormatPreferences preferences, MediaOutputPreference output) => Prepare(item, preferences, output: output);

    internal static string CollectionAvailabilityText(MediaDownloadChoice? choice, string? error) => choice is { } job
        ? $"Ready · {job.DisplayContainer} · {job.DisplayDetail}"
        : $"Skipped · {Limit(error ?? "No matching format", 75)}";

    internal static string CollectionRowText(string title, string availability) =>
        $"{Limit(title, 54)} · {Limit(availability, 90)}";

    private void UpdateCollectionActions()
    {
        if (!IsDisposed && _state == ViewState.Collection) SetButtonsEnabled(_entries.CheckedIndices.Count > 0);
    }

    private async Task MakeSelectionsAsync(bool queueOnly)
    {
        if (_busy) return;
        SetBusy(true, _collection is null ? "Checking selected format…" : "Preparing selected entries under the chosen quality…");
        try
        {
            var prefs = CurrentPreferences();
            MediaOutputPreference output = OutputPreference(prefs);
            var jobs = new List<MediaDownloadChoice>();
            var available = new List<string>();
            var errors = new List<string>();
            if (_collection is null && _item is { } single)
            {
                var prepared = PrepareCurrentChoice();
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
                        preparedItems[i] = result.Item is { } item ? PrepareCollectionEntry(item, prefs, output)
                            : (null, result.Error?.UserMessage ?? "Unavailable");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { preparedItems[i] = (null, "This item could not be prepared."); }
                    ((IProgress<int>)progress).Report(Interlocked.Increment(ref completed));
                }), _lifetime.Token);
                for (int i = 0; i < preparedItems.Length; i++)
                {
                    string title = Limit(collection[selected[i]].Title ?? collection[selected[i]].MediaId, 35);
                    if (preparedItems[i].Choice is { } choice)
                    {
                        jobs.Add(choice);
                        available.Add($"{title}: {choice.DisplayContainer} · {Limit(choice.DisplayDetail, 48)}");
                    }
                    else errors.Add($"{title}: {Limit(preparedItems[i].Error ?? "Unavailable", 120)}");
                }
                SetCollectionAvailability(selected, preparedItems);
            }
            if (IsDisposed || _lifetime.IsCancellationRequested) return;
            if (jobs.Count == 0)
            {
                _status.Text = _collection is null ? "The selected format isn't available." : "No selected entries match this quality.";
                _notice.Text = _collection is null
                    ? Limit(errors.FirstOrDefault() ?? "Try a different quality or source.", 150)
                    : "Skipped entries are marked in the list. Change the quality or selection and try again.";
                return;
            }
            if (_collection is not null)
            {
                _status.Text = $"{jobs.Count} ready; {errors.Count} will be skipped under {QualityName(prefs)}.";
                _notice.Text = "Each checked entry uses its best matching format. Rows show which entries are ready or skipped.";
                if (errors.Count > 0)
                {
                    string availableSummary = string.Join("\n", available.Take(4));
                    string skippedSummary = string.Join("\n", errors.Take(4));
                    if (available.Count > 4) availableSummary += $"\n…and {available.Count - 4} more ready entries; see the list.";
                    if (errors.Count > 4) skippedSummary += $"\n…and {errors.Count - 4} more skipped entries; see the list.";
                    string message = $"{jobs.Count} selected item(s) are ready; {errors.Count} will be skipped. Quality is matched separately for each item.\n\n" +
                        $"Ready:\n{availableSummary}\n\nSkipped:\n{skippedSummary}\n\nAdd only the ready items?";
                    if (VesperMessageBox.Show(this, message, "Partial media selection",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                }
            }
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
            Selections = MediaTransferBridge.ExpandSubtitleChoices(jobs);
            QueueOnly = queueOnly;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!IsDisposed) _status.Text = "The selected media couldn't be prepared."; }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private static string QualityName(FormatPreferences preferences) => preferences.Mode switch
    {
        QualityMode.Balanced => "Balanced",
        QualityMode.Smallest => "Smallest",
        QualityMode.AudioOnly => "Audio only",
        QualityMode.Custom => preferences.TargetHeight is { } height ? $"{height}p" : "Custom",
        _ => "Best quality"
    };

    internal static string ClipBehaviorNotice(MediaOutput output, JobKind kind) =>
        ClipSelection.Notice(ClipSelection.ModeFor(output, kind));

    internal static string ClipRangeSummary(double start, double end, double duration) =>
        $"{ClipSelection.TimeText(start)} to {ClipSelection.TimeText(end)} selected " +
        $"({ClipSelection.TimeText(end - start)} of {ClipSelection.TimeText(duration)})";

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
        _audioTrack.Enabled = !_busy;
        _subtitleChoice.Enabled = !_busy;
        _subtitleLanguages.Enabled = !_busy;
        _embedSubtitles.Enabled = !_busy;
        _clipStart.Enabled = _clipEnd.Enabled = _clipBar.Enabled = !_busy;
        if (_busy) _clipCheck.Enabled = false;
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
