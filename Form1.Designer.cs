#nullable enable

namespace VESPER;

// The Designer owns layout. Form1.cs owns state, data and event handlers.
partial class Form1
{
    private System.ComponentModel.IContainer? components = null;
    private Panel _mast = null!;
    private Panel _toolbar = null!;
    private Panel _normalTools = null!;
    private Panel _selectedTools = null!;
    private Panel _footer = null!;
    private VesperSearchFrame _searchFrame = null!;
    private TextBox _search = null!;
    private VesperChoice _filter = null!;
    private VesperChoice _sort = null!;
    private Label _showLabel = null!;
    private Label _sortLabel = null!;
    private Label _footerLabel = null!;
    private Label _selectedLabel = null!;
    private Label _brandText = null!;
    private Label _brandCaption = null!;
    private VesperButton _add = null!;
    private VesperButton _settings = null!;
    private VesperButton _pause = null!;
    private VesperButton _resume = null!;
    private VesperButton _remove = null!;
    private VesperButton _clear = null!;
    private PictureBox _brandIcon = null!;
    private DownloadGrid _grid = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing) components?.Dispose();
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        _mast = new VesperPanel();
        _brandIcon = new PictureBox();
        _brandText = new Label();
        _brandCaption = new Label();
        _add = new VesperButton();
        _settings = new VesperButton();
        _toolbar = new Panel();
        _normalTools = new Panel();
        _searchFrame = new VesperSearchFrame();
        _search = new TextBox();
        _showLabel = new Label();
        _filter = new VesperChoice();
        _sortLabel = new Label();
        _sort = new VesperChoice();
        _selectedTools = new Panel();
        _selectedLabel = new Label();
        _pause = new VesperButton();
        _resume = new VesperButton();
        _remove = new VesperButton();
        _clear = new VesperButton();
        _footer = new VesperPanel();
        _footerLabel = new Label();
        _grid = new DownloadGrid();
        _mast.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)_brandIcon).BeginInit();
        _toolbar.SuspendLayout();
        _normalTools.SuspendLayout();
        _searchFrame.SuspendLayout();
        _selectedTools.SuspendLayout();
        _footer.SuspendLayout();
        SuspendLayout();
        // Masthead: native caption/title bar remains outside this panel.
        _mast.Name = "mastheadPanel";
        _mast.Dock = DockStyle.Top;
        _mast.BackColor = Color.White;
        _mast.Size = new Size(1010, 76);
        _mast.Controls.AddRange(new Control[] {_brandIcon, _brandText, _brandCaption, _add, _settings});
        _brandIcon.Name = "mothMark";
        _brandIcon.Location = new Point(21, 11);
        _brandIcon.Size = new Size(57, 53);
        _brandIcon.SizeMode = PictureBoxSizeMode.Zoom;
        _brandIcon.TabStop = false;
        _brandIcon.BackColor = Color.Transparent;
        _brandText.Name = "wordmark";
        _brandText.AutoSize = true;
        _brandText.Location = new Point(90, 13);
        _brandText.Font = new Font("Georgia", 21.75F);
        _brandText.ForeColor = Color.FromArgb(40, 50, 41);
        _brandText.Text = "VESPER";
        _brandCaption.Name = "brandSubtitle";
        _brandCaption.AutoSize = true;
        _brandCaption.Location = new Point(91, 49);
        _brandCaption.Font = new Font("Segoe UI", 7.2F);
        _brandCaption.ForeColor = Color.FromArgb(104, 116, 107);
        _brandCaption.Text = "D O W N L O A D   M A N A G E R";
        _add.Name = "addDownloadButton";
        _add.Text = "Add download";
        _add.Primary = true;
        _add.Symbol = VesperIcon.Plus;
        _add.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _add.Location = new Point(807, 21); _add.Size = new Size(150, 34);
        _add.TabIndex = 0;
        _settings.Name = "settingsButton";
        _settings.Text = "";
        _settings.Symbol = VesperIcon.Gear;
        _settings.AccessibleName = "Settings";
        _settings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _settings.Location = new Point(970, 21); _settings.Size = new Size(34, 34);
        _settings.TabIndex = 1;
        // Toolbar
        _toolbar.Name = "toolbarPanel";
        _toolbar.Dock = DockStyle.Top;
        _toolbar.BackColor = Color.FromArgb(248, 249, 246);
        _toolbar.Size = new Size(1010, 57);
        _normalTools.Name = "searchAndFilterPanel";
        _normalTools.Dock = DockStyle.Fill;
        _normalTools.Controls.AddRange(new Control[] {_searchFrame, _showLabel, _filter, _sortLabel, _sort});
        _searchFrame.Name = "searchBoxFrame";
        _searchFrame.BackColor = Color.White;
        _searchFrame.Location = new Point(24, 12);
        _searchFrame.Size = new Size(405, 32);
        _searchFrame.Controls.Add(_search);
        _search.Name = "searchDownloads";
        _search.BorderStyle = BorderStyle.None;
        _search.BackColor = Color.White;
        _search.Font = new Font("Segoe UI", 9F);
        _search.Location = new Point(36, 7);
        _search.Size = new Size(355, 19);
        _search.PlaceholderText = "Search downloads";
        _showLabel.Name = "showLabel";
        _showLabel.AutoSize = true;
        _showLabel.Text = "Show";
        _showLabel.ForeColor = Color.FromArgb(104, 116, 107);
        _showLabel.Location = new Point(661, 20);
        _filter.Name = "showFilter";
        _filter.Text = "Everything";
        _filter.Location = new Point(696, 12); _filter.Size = new Size(143, 32);
        _filter.TabIndex = 2;
        _sortLabel.Name = "sortLabel";
        _sortLabel.AutoSize = true;
        _sortLabel.Text = "Sort";
        _sortLabel.ForeColor = Color.FromArgb(104, 116, 107);
        _sortLabel.Location = new Point(847, 20);
        _sort.Name = "sortChoice";
        _sort.Text = "Newest first";
        _sort.Location = new Point(880, 12); _sort.Size = new Size(120, 32);
        _sort.TabIndex = 3;
        _selectedTools.Name = "selectedActionsPanel";
        _selectedTools.Dock = DockStyle.Fill;
        _selectedTools.Visible = false;
        _selectedTools.Controls.AddRange(new Control[] {_selectedLabel, _pause, _resume, _remove, _clear});
        _selectedLabel.Name = "selectedCountLabel";
        _selectedLabel.AutoSize = true;
        _selectedLabel.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        _selectedLabel.Text = "0 selected";
        _selectedLabel.Location = new Point(24, 20);
        _pause.Text = "Pause"; _pause.Symbol = VesperIcon.Pause; _pause.Location = new Point(126, 12); _pause.Size = new Size(86, 32); _pause.Name = "pauseSelected";
        _resume.Text = "Resume"; _resume.Symbol = VesperIcon.Play; _resume.Location = new Point(219, 12); _resume.Size = new Size(86, 32); _resume.Name = "resumeSelected";
        _remove.Text = "Remove"; _remove.Symbol = VesperIcon.Trash; _remove.Location = new Point(312, 12); _remove.Size = new Size(86, 32); _remove.Name = "removeSelected";
        _clear.Text = "Clear selection"; _clear.Symbol = VesperIcon.Close; _clear.Location = new Point(850, 12); _clear.Size = new Size(138, 32); _clear.Name = "clearSelected";
        _toolbar.Controls.Add(_selectedTools);
        _toolbar.Controls.Add(_normalTools);
        // Virtualized painted list. No cells, native grid borders, or per-row controls.
        _grid.Name = "downloadList";
        _grid.Dock = DockStyle.Fill;
        _grid.TabIndex = 4;
        _footer.Name = "footerPanel";
        _footer.Dock = DockStyle.Bottom;
        _footer.Size = new Size(1010, 30);
        _footer.BackColor = Color.FromArgb(242, 244, 239);
        _footerLabel.Name = "statusText";
        _footerLabel.Dock = DockStyle.Fill;
        _footerLabel.Padding = new Padding(21, 0, 0, 0);
        _footerLabel.Font = new Font("Segoe UI", 8F);
        _footerLabel.ForeColor = Color.FromArgb(104, 116, 107);
        _footerLabel.Text = "";
        _footerLabel.TextAlign = ContentAlignment.MiddleLeft;
        _footer.Controls.Add(_footerLabel);
        // Form
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.White;
        ClientSize = new Size(950, 625);
        Font = new Font("Segoe UI", 9F);
        KeyPreview = true;
        MinimumSize = new Size(830, 540);
        Name = "Form1";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "VESPER — Download Manager";
        Controls.Add(_grid);
        Controls.Add(_footer);
        Controls.Add(_toolbar);
        Controls.Add(_mast);
        _mast.ResumeLayout(false);
        _mast.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)_brandIcon).EndInit();
        _searchFrame.ResumeLayout(false);
        _searchFrame.PerformLayout();
        _normalTools.ResumeLayout(false);
        _normalTools.PerformLayout();
        _selectedTools.ResumeLayout(false);
        _selectedTools.PerformLayout();
        _toolbar.ResumeLayout(false);
        _footer.ResumeLayout(false);
        ResumeLayout(false);
        PerformLayout();
    }
    #endregion
}
