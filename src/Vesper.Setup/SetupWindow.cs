using System.Diagnostics;
using VESPER;

namespace Vesper.Setup;

internal sealed class SetupWindow : Form
{
    private enum View { Ready, Working, Complete, Failed, Cancelled }

    private readonly SetupMode _mode;
    private readonly InstallPaths _paths;
    private readonly Action<IProgress<InstallProgress>, CancellationToken> _work;
    private readonly FirstFlightCanvas _canvas;
    private readonly Icon _icon;
    private readonly VesperButton _action = new() { Primary = true, TabIndex = 0 };
    private readonly VesperButton _skip = new() { Text = "Skip intro", TabIndex = 1 };
    private readonly VesperButton _cancel = new() { Text = "Cancel", TabIndex = 2 };
    private readonly VesperButton _extension = new() { Text = "Extension files", Symbol = VesperIcon.Folder, TabIndex = 1 };
    private readonly Label _status = new() { TextAlign = ContentAlignment.MiddleCenter, UseMnemonic = false, AutoEllipsis = true };
    private readonly Label _note = new() { TextAlign = ContentAlignment.MiddleCenter, UseMnemonic = false, AutoEllipsis = true };
    private readonly ProgressBar _meter = new() { AccessibleName = "Verified installation files", Style = ProgressBarStyle.Continuous };
    private readonly ToolTip _tips = new();
    private readonly Font _statusFont = new("Segoe UI", 10.5f);
    private readonly Font _noteFont = new("Segoe UI", 9f);
    private VesperPalette _palette;
    private CancellationTokenSource? _cancellation;
    private View _view;
    private InstallProgress _progress;
    private string _error = "";
    private bool _closeAfterCancel;

    internal SetupWindow(SetupMode mode, InstallPaths paths, VesperPalette palette, Action<IProgress<InstallProgress>, CancellationToken> work)
    {
        _mode = mode;
        _paths = paths;
        _work = work;
        _palette = palette;
        Text = "VESPER Setup";
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        KeyPreview = true;
        ClientSize = new Size(FirstFlightCanvas.LogicalWidth, FirstFlightCanvas.LogicalHeight);
        using (var stream = InstallPayload.Resource("Vesper.Setup.App.ico"))
        using (var icon = new Icon(stream))
            Icon = _icon = (Icon)icon.Clone();

        _canvas = new FirstFlightCanvas(palette) { Dock = DockStyle.Fill };
        _canvas.IntroFinished += IntroFinished;
        _status.Font = _statusFont;
        _note.Font = _noteFont;
        Controls.AddRange([_action, _skip, _cancel, _extension, _status, _note, _meter, _canvas]);
        _action.Click += (_, _) => StartOrOpen();
        _skip.Click += (_, _) => SkipIntro();
        _cancel.Click += (_, _) => RequestCancellation();
        _extension.Click += (_, _) => OpenPath(Path.Combine(_paths.App, "extension"), shell: true);
        _tips.SetToolTip(_extension, "In Chrome, open Extensions, turn on Developer mode, and choose Load unpacked.");
        AcceptButton = _action;
        ApplyPalette();
        RefreshControls();
    }

    private async void StartOrOpen()
    {
        if (_view == View.Complete)
        {
            if (_mode == SetupMode.Uninstall || OpenPath(_paths.Executable, shell: false)) Close();
            return;
        }
        if (_view == View.Working) return;
        await RunAttemptAsync(Program.MotionEnabled);
    }

    internal async Task RunAttemptAsync(bool animate)
    {
        if (_view == View.Working) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _view = View.Working;
        _progress = new(_mode == SetupMode.Uninstall ? "Preparing to remove VESPER…" : "Preparing VESPER…");
        if (_mode == SetupMode.Install && animate) _canvas.StartIntro();
        RefreshControls();
        if (_canvas.Playing) _skip.Focus();
        else _cancel.Focus();

        var progress = new Progress<InstallProgress>(update =>
        {
            if (_view != View.Working || _cancellation != cancellation || cancellation.IsCancellationRequested) return;
            _progress = update;
            if (!_canvas.Playing) RefreshControls();
        });
        try
        {
            await Task.Run(() => _work(progress, cancellation.Token));
            _view = View.Complete;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { _view = View.Cancelled; }
        catch (Exception ex)
        {
            _view = View.Failed;
            _error = ex.Message;
        }
        finally
        {
            _cancellation = null;
            if (_view != View.Complete || _mode != SetupMode.Install) _canvas.StopIntro();
            RefreshControls();
            if (_view == View.Cancelled && _closeAfterCancel) Close();
            else _action.Focus();
        }
    }

    private void RefreshControls()
    {
        bool working = _view == View.Working;
        bool intro = _canvas.Playing;
        _action.Visible = !working;
        _cancel.Visible = working && !intro;
        _cancel.Enabled = _cancellation?.IsCancellationRequested == false;
        _skip.Visible = intro;
        _extension.Visible = _view == View.Complete && _mode != SetupMode.Uninstall;
        _meter.Visible = working && !intro && _progress.Total > 0;
        if (_meter.Visible) _meter.Value = (int)Math.Clamp(100d * _progress.Verified / _progress.Total, 0, 100);
        _status.Visible = !intro;
        _note.Visible = !intro;
        _status.ForeColor = _view == View.Failed ? _palette.Red : _palette.Text;
        _action.Text = _view switch
        {
            View.Complete => _mode == SetupMode.Uninstall ? "Close setup" : "Open VESPER",
            View.Failed or View.Cancelled => "Try again",
            _ when _mode == SetupMode.Uninstall => "Uninstall VESPER",
            _ when _mode == SetupMode.Repair => "Repair VESPER",
            _ when File.Exists(_paths.Executable) => "Install update",
            _ => "Install VESPER"
        };
        _status.Text = _view switch
        {
            View.Working => _progress.Activity,
            View.Complete => _mode == SetupMode.Uninstall ? "VESPER was removed." : "Installation complete",
            View.Failed => _error,
            View.Cancelled => "Setup canceled.",
            _ when _mode == SetupMode.Uninstall => "Remove VESPER from this Windows account?",
            _ when _mode == SetupMode.Repair => "Restore VESPER’s application files and browser helper.",
            _ => "A home for your downloads."
        };
        _tips.SetToolTip(_status, _view == View.Failed ? _error : null);
        _note.Text = _view switch
        {
            View.Ready when _mode == SetupMode.Uninstall => "Your downloads, settings, and history will be kept.",
            View.Ready when _mode == SetupMode.Install => "For this Windows account only.",
            View.Complete when _mode != SetupMode.Uninstall => "In Chrome, use Developer mode → Load unpacked to enable the bundled extension.",
            View.Complete => "Your downloads, settings, and history were kept.",
            _ => ""
        };
        AcceptButton = intro && working ? _skip : working ? null : _action;
    }

    private void IntroFinished()
    {
        bool skipHadFocus = _skip.Focused;
        RefreshControls();
        if (!skipHadFocus) return;
        if (_view == View.Working) _cancel.Focus();
        else _action.Focus();
    }

    private void SkipIntro()
    {
        _canvas.StopIntro();
        RefreshControls();
        if (_view == View.Working) _cancel.Focus();
        else _action.Focus();
    }

    private void RequestCancellation()
    {
        _cancellation?.Cancel();
        _canvas.StopIntro();
        _progress = new("Canceling setup…");
        RefreshControls();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _canvas.Playing)
        {
            SkipIntro();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_view == View.Working)
        {
            e.Cancel = true;
            _closeAfterCancel = true;
            RequestCancellation();
        }
        base.OnFormClosing(e);
    }

    private void ApplyPalette()
    {
        BackColor = _palette.Back;
        _status.BackColor = _note.BackColor = BackColor;
        _note.ForeColor = _palette.Muted;
        foreach (var button in new[] { _action, _cancel, _skip, _extension }) button.ApplyPalette(_palette);
        _canvas.SetPalette(_palette);
        if (IsHandleCreated) VesperWindowChrome.Apply(this, _palette);
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        if (_canvas is null) return;
        _palette = UiKit.Palette(UiKit.IsSystemDark(), botanical: true);
        ApplyPalette();
        if (_canvas.Playing && !Program.MotionEnabled) SkipIntro();
        else RefreshControls();
    }

    protected override void OnLoad(EventArgs e)
    {
        FitToScreen(DeviceDpi);
        VesperWindowChrome.Apply(this, _palette);
        base.OnLoad(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitToScreen(e.DeviceDpiNew);
    }

    private void FitToScreen(int dpi)
    {
        Rectangle work = Screen.FromControl(this).WorkingArea;
        float scale = Math.Min(dpi / 96f, Math.Min((work.Width - 24f) / FirstFlightCanvas.LogicalWidth,
            (work.Height - 68f) / FirstFlightCanvas.LogicalHeight));
        int Px(int value) => (int)Math.Round(value * scale);
        SuspendLayout();
        ClientSize = new Size(Px(FirstFlightCanvas.LogicalWidth), Px(FirstFlightCanvas.LogicalHeight));
        _action.SetBounds(Px(220), Px(357), Px(360), Px(43));
        _skip.SetBounds(Px(675), Px(14), Px(110), Px(32));
        _cancel.SetBounds(Px(339), Px(396), Px(122), Px(32));
        _extension.SetBounds(Px(600), Px(357), Px(153), Px(43));
        _status.SetBounds(Px(44), Px(300), Px(712), Px(54));
        _note.SetBounds(Px(34), Px(412), Px(732), Px(32));
        _meter.SetBounds(Px(220), Px(365), Px(360), Px(12));
        ResumeLayout();
        Location = new Point(Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width)),
            Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height)));
    }

    private bool OpenPath(string path, bool shell)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = shell, WorkingDirectory = _paths.App });
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _status.Text = ex.Message;
            return false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
            _tips.Dispose();
            _statusFont.Dispose();
            _noteFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
