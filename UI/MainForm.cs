using System.Drawing;
using System.Drawing.Drawing2D;
using LangKinhHuKhong.Editer.Core;

namespace LangKinhHuKhong.Editer.UI;

public sealed class MainForm : Form
{
    // Bảng màu Pro Studio Dark Slate (tương phản chuẩn phòng dựng chuyên nghiệp, không chói mắt)
    private static readonly Color Background = Color.FromArgb(14, 19, 27);
    private static readonly Color SurfaceDark = Color.FromArgb(18, 24, 34);
    private static readonly Color CardColor = Color.FromArgb(22, 30, 43);
    private static readonly Color InputColor = Color.FromArgb(15, 21, 31);
    private static readonly Color Foreground = Color.FromArgb(241, 245, 249);
    private static readonly Color Muted = Color.FromArgb(148, 163, 184);
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);
    private static readonly Color AccentSuccess = Color.FromArgb(16, 185, 129);
    private static readonly Color AccentWarning = Color.FromArgb(245, 158, 11);

    private readonly ListBox _videos = NewListBox();
    private readonly ListBox _mp3s = NewListBox();
    private readonly ListBox _srts = NewListBox();
    private readonly Label _srtCount = NewLabel("0 SRT", 10, Muted);
    private readonly ComboBox _srtMode = NewCombo();
    private readonly CheckBox _burnSrt = new();
    private readonly CheckBox _exportSrt = new();
    private readonly ComboBox _subFont = NewCombo();
    private readonly NumericUpDown _subFontSize = NewNumber(28, 14, 80);
    private readonly NumericUpDown _subOffset = new() { Minimum = -10000, Maximum = 10000, Increment = 100, Width = 90 };
    private readonly TextBox _srtPreview = NewTextBox();

    // Controls cho Tính năng Ngoài luồng: Tạo .SRT từ 1 file MP3 hoặc 1 file Video
    private readonly TextBox _genSrtInputPath = NewTextBox();
    private readonly TextBox _genSrtOutputPath = NewTextBox();
    private readonly ComboBox _genSrtMode = NewCombo();
    private readonly ComboBox _genSrtLang = NewCombo();
    private readonly CheckBox _genKeepVideoMp3 = new();
    private readonly TextBox _genSrtScriptBox = NewTextBox();
    private readonly Label _genSrtFlowHint = NewLabel("Chọn 1 tệp MP3 hoặc Video (.mp4/.mkv/...) để tạo phụ đề .SRT khớp thời gian thực.", 9, Muted);
    private readonly StudioButton _genSrtRunBtn = NewButton("⚡  TẠO FILE .SRT NGAY", true);
    private readonly StudioButton _genSrtSaveEditsBtn = NewButton("💾  LƯU NỘI DUNG .SRT", false);
    private readonly StudioButton _genSrtPushToRenderBtn = NewButton("➕  ĐƯA SANG RENDER VIDEO", false);
    private readonly StudioButton _genSrtOpenFileBtn = NewButton("📂  MỞ VỊ TRÍ FILE", false);
    private string? _lastGeneratedSrtPath;
    private string? _lastExtractedMp3Path;

    private StudioButton? _navRenderBtn;
    private StudioButton? _navCreateSrtBtn;
    private Label? _sidebarHint;
    private Panel? _workspaceHost;
    private Panel? _createSrtScreen;
    private Panel? _srtCard;
    private Panel? _videoCard;
    private Panel? _mp3Card;
    private Panel? _overlayCard;
    private Panel? _settingsCard;
    private Panel? _previewCard;
    private Panel? _scrollArea;
    private PictureBox? _logoBox;
    private readonly Label _videoCount = NewLabel("0 video", 10, Muted);
    private readonly Label _mp3Count = NewLabel("0 MP3", 10, Muted);
    private readonly TextBox _image1Path = NewTextBox();
    private readonly TextBox _image2Path = NewTextBox();
    private readonly TextBox _image3Path = NewTextBox();
    private readonly TextBox _image4Path = NewTextBox();
    private readonly PictureBox _preview1 = NewPreview();
    private readonly PictureBox _preview2 = NewPreview();
    private readonly PictureBox _preview3 = NewPreview();
    private readonly PictureBox _preview4 = NewPreview();
    private readonly PictureBox _layoutPreview = NewPreview();
    private readonly OverlaySlot[] _overlaySlots;
    private readonly TextBox _folderPath = NewTextBox();
    private readonly ComboBox _resolution = NewCombo();
    private readonly ComboBox _encoder = NewCombo();
    private readonly CheckBox _muteSourceAudio = new();
    private readonly ComboBox _selectedVideoSpeed = NewCombo();
    private readonly StudioButton _applySpeed = NewButton("ÁP DỤNG", true);
    private readonly ProgressBar _progress = new();
    private readonly Label _status = NewLabel("Sẵn sàng dựng video hoặc tạo phụ đề SRT.", 10, Muted);
    private readonly Label _ffmpegStatus = NewLabel("FFmpeg: đang kiểm tra...", 10, Muted);
    private readonly StudioButton _render = NewButton("▶  XUẤT VIDEO MP4", true);
    private readonly StudioButton _cancel = NewButton("HỦY TÁC VỤ", false);
    private readonly StudioButton _openFolder = NewButton("MỞ THƯ MỤC", false);
    private readonly List<Control> _editableControls = [];
    private readonly ToolTip _tooltip = new();
    private CancellationTokenSource? _cts;
    private bool _rendering;
    private bool _closeAfterCancel;
    private string _previousStage = "";

    public MainForm()
    {
        Text = "LangKinhHuKhong.Editer v4.2 Pro  |  Multi-Core Video & Subtitle Studio";
        MinimumSize = new Size(1080, 740);
        Size = new Size(1540, 980);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = Foreground;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        LoadWindowIcon();
        _overlaySlots =
        [
            new OverlaySlot(Corner.TopLeft, "04-1  ẢNH TRÊN TRÁI", _image1Path, _preview1, NewNumber(20, 20, 100), NewNumber(20, 20, 100), NewNumber(1, 0, 100), NewNumber(1, 0, 100)),
            new OverlaySlot(Corner.TopRight, "04-2  ẢNH TRÊN PHẢI", _image2Path, _preview2, NewNumber(20, 20, 100), NewNumber(20, 20, 100), NewNumber(1, 0, 100), NewNumber(1, 0, 100)),
            new OverlaySlot(Corner.BottomLeft, "04-3  ẢNH DƯỚI TRÁI", _image3Path, _preview3, NewNumber(20, 20, 100), NewNumber(20, 20, 100), NewNumber(1, 0, 100), NewNumber(1, 0, 100)),
            new OverlaySlot(Corner.BottomRight, "04-4  ẢNH DƯỚI PHẢI", _image4Path, _preview4, NewNumber(20, 20, 100), NewNumber(20, 20, 100), NewNumber(1, 0, 100), NewNumber(1, 0, 100))
        ];

        BuildInterface();
        WireEvents();
        _folderPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "LangKinhHuKhong.Output");
        CheckFfmpeg();
    }

    private void BuildInterface()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            BackColor = Background, Margin = Padding.Empty, Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        Controls.Add(root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = SurfaceDark,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(22, 10, 24, 10)
        };
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 290));

        var logoHolder = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = SurfaceDark,
            Margin = Padding.Empty,
            Padding = new Padding(1, 3, 12, 3)
        };
        var logoBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            BackColor = SurfaceDark,
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = Padding.Empty,
            TabStop = false
        };
        _logoBox = logoBox;
        TryLoadAppLogo(logoBox);
        logoHolder.Controls.Add(logoBox);
        header.Controls.Add(logoHolder, 0, 0);

        var brandText = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = SurfaceDark,
            Margin = Padding.Empty
        };
        var brandTitle = new Label
        {
            Text = "LangKinhHuKhong.Editer Pro",
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Foreground,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 18F, FontStyle.Bold)
        };
        var brandCaption = new Label
        {
            Text = "LĂNG KÍNH HƯ KHÔNG  ·  MULTI-CORE VIDEO & AI SUBTITLE STUDIO",
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Muted,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular)
        };
        brandText.Controls.Add(brandTitle);
        brandText.Controls.Add(brandCaption);
        void LayoutBrandText()
        {
            var titleH = brandTitle.Font.Height + 8;
            var captionH = brandCaption.Font.Height + 6;
            var totalH = titleH + captionH + 2;
            var y = Math.Max(0, (brandText.ClientSize.Height - totalH) / 2);
            var w = Math.Max(0, brandText.ClientSize.Width - 6);
            brandTitle.SetBounds(0, y, w, titleH);
            brandCaption.SetBounds(0, y + titleH + 2, w, captionH);
        }
        brandText.Resize += (_, _) => LayoutBrandText();
        header.Controls.Add(brandText, 1, 0);

        var statusPanel = new StudioCard
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(24, 34, 49),
            BorderColor = Color.FromArgb(45, 62, 88),
            CornerRadius = 8,
            Margin = new Padding(10, 14, 0, 14),
            Padding = new Padding(8, 6, 8, 6)
        };
        var statusTitle = new Label
        {
            Text = $"●  TĂNG TỐC ĐA LUỒNG ({Environment.ProcessorCount} CPU CORES)",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.BottomCenter,
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(96, 165, 250),
            Font = new Font("Segoe UI", 8.75F, FontStyle.Bold)
        };
        var statusDescription = new Label
        {
            Text = "Xuất MP4 720p/1080p/4K  ·  Tạo SRT tự động",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 8.5F)
        };
        statusPanel.Controls.Add(statusDescription);
        statusPanel.Controls.Add(statusTitle);
        header.Controls.Add(statusPanel, 2, 0);
        root.Controls.Add(header, 0, 0);

        var central = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2,
            BackColor = Background, Padding = new Padding(0, 2, 0, 0)
        };
        central.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 226));
        central.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var sidebar = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = SurfaceDark,
            Padding = new Padding(14, 16, 14, 14)
        };
        var menu = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = SurfaceDark
        };
        var navTitle = NewLabel("CHẾ ĐỘ LÀM VIỆC", 9, Muted, true);
        navTitle.AutoSize = false; navTitle.Width = 192; navTitle.Height = 34;
        menu.Controls.Add(navTitle);
        _navRenderBtn = AddNav(menu, "🎬   Render Video", () => SwitchScreen(isRenderScreen: true), active: true);
        _navCreateSrtBtn = AddNav(menu, "📝   Create .SRT", () => SwitchScreen(isRenderScreen: false), active: false);

        _sidebarHint = NewLabel("", 8.75f, Muted);
        _sidebarHint.Width = 194; _sidebarHint.Height = 260; _sidebarHint.Margin = new Padding(2, 18, 0, 0);
        menu.Controls.Add(_sidebarHint);
        sidebar.Controls.Add(menu);
        central.Controls.Add(sidebar, 0, 0);

        _workspaceHost = new Panel { Dock = DockStyle.Fill, BackColor = Background, Margin = Padding.Empty, Padding = Padding.Empty };
        central.Controls.Add(_workspaceHost, 1, 0);

        // MÀN HÌNH 1: RENDER VIDEO
        _scrollArea = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Background };
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 7, Padding = new Padding(18, 14, 18, 18), BackColor = Background
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 365));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 305));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 296));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 296));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 255));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        _scrollArea.Controls.Add(body);

        _videoCard = CreateMediaCard("01  VIDEO NGUỒN", _videos, _videoCount, true);
        _mp3Card = CreateMediaCard("02  AUDIO MP3", _mp3s, _mp3Count, false);
        body.Controls.Add(_videoCard, 0, 0);
        body.Controls.Add(_mp3Card, 1, 0);

        _srtCard = CreateSubtitleCard();
        body.Controls.Add(_srtCard, 0, 1);
        body.SetColumnSpan(_srtCard, 2);

        _overlayCard = CreateOverlayCard(_overlaySlots[0]);
        body.Controls.Add(_overlayCard, 0, 2);
        body.Controls.Add(CreateOverlayCard(_overlaySlots[1]), 1, 2);
        body.Controls.Add(CreateOverlayCard(_overlaySlots[2]), 0, 3);
        body.Controls.Add(CreateOverlayCard(_overlaySlots[3]), 1, 3);

        _previewCard = CreateLayoutPreviewCard();
        body.Controls.Add(_previewCard, 0, 4);
        body.SetColumnSpan(_previewCard, 2);

        _settingsCard = CreateSettingsCard();
        body.Controls.Add(_settingsCard, 0, 5);
        body.SetColumnSpan(_settingsCard, 2);

        var note = NewLabel("0–4 PNG cố định theo 4 góc • Phụ đề luôn nằm lớp trên cùng • Tối ưu đa nhân CPU & RAM • Xuất 1 MP4 duy nhất", 9.5f, Muted);
        note.Margin = new Padding(10, 4, 0, 0);
        body.Controls.Add(note, 0, 6);
        body.SetColumnSpan(note, 2);

        // MÀN HÌNH 2: CREATE .SRT (Độc lập hoàn toàn)
        _createSrtScreen = CreateStandaloneSrtScreen();
        _createSrtScreen.Visible = false;

        _workspaceHost.Controls.Add(_scrollArea);
        _workspaceHost.Controls.Add(_createSrtScreen);
        root.Controls.Add(central, 0, 1);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = new Padding(22, 8, 22, 10), BackColor = SurfaceDark
        };
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _status.Dock = DockStyle.Fill;
        footer.Controls.Add(_status, 0, 0);
        _progress.Minimum = 0; _progress.Maximum = 1000;
        _progress.Style = ProgressBarStyle.Continuous; _progress.Dock = DockStyle.Fill;
        footer.Controls.Add(_progress, 0, 1);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Padding = new Padding(0, 8, 0, 0), BackColor = SurfaceDark
        };
        _render.Size = new Size(218, 44);
        _cancel.Size = new Size(124, 44);
        _openFolder.Size = new Size(152, 44);
        _cancel.Enabled = false;
        buttons.Controls.AddRange([_render, _cancel, _openFolder, _ffmpegStatus]);
        _ffmpegStatus.AutoSize = true;
        _ffmpegStatus.Padding = new Padding(0, 12, 14, 0);
        footer.Controls.Add(buttons, 0, 2);
        root.Controls.Add(footer, 0, 2);

        SwitchScreen(isRenderScreen: true);
    }

    private static StudioButton AddNav(FlowLayoutPanel menu, string title, Action click, bool active = false)
    {
        var item = NewButton(title, active);
        item.Width = 196;
        item.Height = 46;
        item.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        item.Margin = new Padding(0, 0, 0, 10);
        item.Padding = new Padding(14, 0, 10, 0);
        item.TextAlign = ContentAlignment.MiddleLeft;
        ApplyNavTabStyle(item, active);
        item.Click += (_, _) => click();
        menu.Controls.Add(item);
        return item;
    }

    private static void ApplyNavTabStyle(StudioButton btn, bool active)
    {
        btn.IsPrimary = active;
        btn.BackColor = active ? Accent : Color.FromArgb(24, 33, 47);
        btn.BorderColor = active ? Color.FromArgb(96, 165, 250) : Color.FromArgb(40, 53, 73);
        btn.HoverBackColor = active ? Color.FromArgb(59, 130, 246) : Color.FromArgb(35, 48, 68);
        btn.PressedBackColor = active ? Color.FromArgb(29, 78, 216) : Color.FromArgb(20, 28, 40);
        btn.ForeColor = active ? Color.White : Color.FromArgb(203, 213, 225);
        btn.Invalidate();
    }

    private void SwitchScreen(bool isRenderScreen)
    {
        if (_scrollArea is not null) _scrollArea.Visible = isRenderScreen;
        if (_createSrtScreen is not null) _createSrtScreen.Visible = !isRenderScreen;
        if (_navRenderBtn is not null) ApplyNavTabStyle(_navRenderBtn, isRenderScreen);
        if (_navCreateSrtBtn is not null) ApplyNavTabStyle(_navCreateSrtBtn, !isRenderScreen);

        _render.Visible = isRenderScreen;
        _openFolder.Visible = isRenderScreen;

        if (_sidebarHint is not null)
        {
            _sidebarHint.Text = isRenderScreen
                ? "MÀN HÌNH RENDER VIDEO:\n\n" +
                  "• Ghép nhiều Video & nhiều MP3 thành 1 file MP4 duy nhất.\n\n" +
                  "• Chuẩn hóa đa luồng CPU/GPU & bộ đệm RAM tốc độ cao.\n\n" +
                  "• Kéo thả tệp trực tiếp vào danh sách hoặc khung Preview 16:9."
                : "MÀN HÌNH CREATE .SRT:\n\n" +
                  "• Luồng 1: Chọn 1 file MP3 ➔ Xuất 1 file .SRT khớp thời gian thực.\n\n" +
                  "• Luồng 2: Chọn 1 file Video ➔ Tự tách MP3 ➔ Xuất 1 file .SRT khớp thời gian thực.\n\n" +
                  "• Có thể sửa trực tiếp phụ đề và đẩy sang màn hình Render Video.";
        }
    }

    /// <summary>
    /// Màn hình số 2 (Create .SRT) độc lập hoàn toàn với màn hình Render Video:
    /// - Chọn 1 file MP3 => xuất 1 file .SRT khớp thời gian thực.
    /// - Chọn 1 file Video => tự động tách ra MP3 (Video -> MP3 -> .SRT) rồi xuất 1 file .SRT khớp thời gian thực.
    /// </summary>
    private Panel CreateStandaloneSrtScreen()
    {
        var screen = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Background,
            Padding = new Padding(18, 14, 18, 18)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Background,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 265));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Card trên: Chọn file MP3 / Video, Cấu hình & Nút thực thi
        var topCard = NewCard();
        topCard.BorderColor = Color.FromArgb(45, 85, 155);
        var topTable = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1 };
        topTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        topTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        topTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        topTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        topTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var headerRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        headerRow.Controls.Add(NewLabel("01  CHỌN TỆP MP3 HOẶC VIDEO ĐỂ TẠO PHỤ ĐỀ .SRT KHỚP THỜI GIAN THỰC", 11.5f, Color.FromArgb(96, 165, 250), true), 0, 0);
        _genSrtFlowHint.TextAlign = ContentAlignment.MiddleRight;
        headerRow.Controls.Add(_genSrtFlowHint, 1, 0);
        topTable.Controls.Add(headerRow, 0, 0);

        var inputRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        inputRow.Controls.Add(NewLabel("Tệp nguồn (MP3/Video):", 10f, Muted), 0, 0);
        _genSrtInputPath.Dock = DockStyle.Fill;
        _genSrtInputPath.ReadOnly = true;
        _genSrtInputPath.PlaceholderText = "Kéo thả hoặc bấm nút bên phải để chọn 1 file .mp3, .wav, .mp4, .mkv, .mov...";
        inputRow.Controls.Add(_genSrtInputPath, 1, 0);
        var pickInputBtn = NewButton("CHỌN MP3 / VIDEO", true);
        pickInputBtn.Dock = DockStyle.Fill;
        pickInputBtn.Margin = new Padding(10, 0, 0, 5);
        pickInputBtn.Click += (_, _) => ChooseStandaloneSrtInput();
        inputRow.Controls.Add(pickInputBtn, 2, 0);
        topTable.Controls.Add(inputRow, 0, 1);

        var outputRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        outputRow.Controls.Add(NewLabel("Đường dẫn xuất .SRT:", 10f, Muted), 0, 0);
        _genSrtOutputPath.Dock = DockStyle.Fill;
        _genSrtOutputPath.PlaceholderText = "Tự động đặt cùng tên và cùng thư mục với tệp MP3/Video gốc (.srt)";
        outputRow.Controls.Add(_genSrtOutputPath, 1, 0);
        var pickOutBtn = NewButton("ĐỔI NƠI LƯU .SRT...", false);
        pickOutBtn.Dock = DockStyle.Fill;
        pickOutBtn.Margin = new Padding(10, 0, 0, 5);
        pickOutBtn.Click += (_, _) => ChooseStandaloneSrtOutput();
        outputRow.Controls.Add(pickOutBtn, 2, 0);
        topTable.Controls.Add(outputRow, 0, 2);

        var modeRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        modeRow.Controls.Add(NewInlineLabel("Phương thức tạo:", leftMargin: 0));
        _genSrtMode.Items.AddRange([
            "Nhận diện giọng nói AI (Speech-to-Text)",
            "Khớp kịch bản văn bản vào sóng âm thực",
            "Tách khung thời gian theo nhịp nói"
        ]);
        _genSrtMode.SelectedIndex = 0;
        _genSrtMode.Width = 265;
        modeRow.Controls.Add(_genSrtMode);

        modeRow.Controls.Add(NewInlineLabel("Ngôn ngữ giọng đọc:", leftMargin: 16));
        _genSrtLang.Items.AddRange([
            "vi-VN (Tiếng Việt)",
            "en-US (Tiếng Anh)",
            "zh-CN (Tiếng Trung)",
            "ja-JP (Tiếng Nhật)",
            "ko-KR (Tiếng Hàn)"
        ]);
        _genSrtLang.SelectedIndex = 0;
        _genSrtLang.Width = 158;
        modeRow.Controls.Add(_genSrtLang);

        _genKeepVideoMp3.Checked = true;
        _genKeepVideoMp3.AutoSize = true;
        _genKeepVideoMp3.ForeColor = Foreground;
        _genKeepVideoMp3.Text = "Lưu kèm file .MP3 khi tách từ Video (Video ➔ MP3 ➔ .SRT)";
        _genKeepVideoMp3.Margin = new Padding(18, 6, 0, 0);
        modeRow.Controls.Add(_genKeepVideoMp3);
        topTable.Controls.Add(modeRow, 0, 3);

        var actionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), WrapContents = false };
        _genSrtRunBtn.Width = 220;
        _genSrtRunBtn.Height = 40;
        _genSrtSaveEditsBtn.Width = 185;
        _genSrtSaveEditsBtn.Height = 40;
        _genSrtSaveEditsBtn.Enabled = false;
        _genSrtPushToRenderBtn.Width = 215;
        _genSrtPushToRenderBtn.Height = 40;
        _genSrtPushToRenderBtn.Enabled = false;
        _genSrtOpenFileBtn.Width = 165;
        _genSrtOpenFileBtn.Height = 40;
        _genSrtOpenFileBtn.Enabled = false;

        _genSrtRunBtn.Click += async (_, _) => await RunStandaloneSrtGenerationAsync();
        _genSrtSaveEditsBtn.Click += async (_, _) => await SaveEditedSrtContentAsync();
        _genSrtPushToRenderBtn.Click += (_, _) => PushGeneratedSrtToRenderList();
        _genSrtOpenFileBtn.Click += (_, _) => OpenGeneratedSrtFile();

        actionRow.Controls.AddRange([_genSrtRunBtn, _genSrtSaveEditsBtn, _genSrtPushToRenderBtn, _genSrtOpenFileBtn]);
        topTable.Controls.Add(actionRow, 0, 4);
        topCard.Controls.Add(topTable);
        layout.Controls.Add(topCard, 0, 0);

        // Card dưới: Trình biên tập kịch bản & kết quả .SRT toàn màn hình
        var bottomCard = NewCard();
        var bottomTable = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        bottomTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        bottomTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var editorHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        editorHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        editorHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        editorHeader.Controls.Add(NewLabel("02  NỘI DUNG PHỤ ĐỀ .SRT / KỊCH BẢN ĐỐI CHIẾU", 11f, Foreground, true), 0, 0);
        var editorSubHint = NewLabel("Có thể dán kịch bản trước khi tạo hoặc chỉnh sửa trực tiếp các mốc thời gian .SRT sau khi xuất", 9f, Muted);
        editorSubHint.TextAlign = ContentAlignment.MiddleRight;
        editorHeader.Controls.Add(editorSubHint, 1, 0);
        bottomTable.Controls.Add(editorHeader, 0, 0);

        _genSrtScriptBox.Dock = DockStyle.Fill;
        _genSrtScriptBox.Multiline = true;
        _genSrtScriptBox.ScrollBars = ScrollBars.Vertical;
        _genSrtScriptBox.Font = new Font("Consolas", 10.5F, FontStyle.Regular);
        _genSrtScriptBox.PlaceholderText =
            "• Để trống ô này nếu muốn AI tự động nhận diện giọng nói từ file MP3 / Video sang .SRT.\r\n" +
            "• Hoặc dán nội dung văn bản / kịch bản vào đây và chọn chế độ 'Khớp kịch bản văn bản vào sóng âm thực' để hệ thống tự căn thời gian Start --> End khớp chính xác theo giọng đọc.\r\n" +
            "• Sau khi tạo xong, toàn bộ nội dung file .SRT sẽ hiển thị tại đây để bạn kiểm tra và chỉnh sửa.";
        bottomTable.Controls.Add(_genSrtScriptBox, 0, 1);
        bottomCard.Controls.Add(bottomTable);
        layout.Controls.Add(bottomCard, 0, 1);

        screen.Controls.Add(layout);
        EnableStandaloneSrtDrop(screen);
        _editableControls.AddRange([
            pickInputBtn, pickOutBtn, _genSrtOutputPath, _genSrtMode, _genSrtLang,
            _genKeepVideoMp3, _genSrtScriptBox, _genSrtRunBtn, _genSrtSaveEditsBtn, _genSrtPushToRenderBtn, _genSrtOpenFileBtn
        ]);
        return screen;
    }

    private void EnableStandaloneSrtDrop(Control root)
    {
        root.AllowDrop = true;
        root.DragEnter += (_, e) =>
        {
            if (_rendering || e.Data?.GetData(DataFormats.FileDrop) is not string[] paths)
            {
                e.Effect = DragDropEffects.None;
                return;
            }
            e.Effect = paths.Any(p => File.Exists(p) && SrtGenerator.IsSupportedInput(p))
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        };
        root.DragDrop += (_, e) =>
        {
            if (_rendering || e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
            var first = paths.FirstOrDefault(p => File.Exists(p) && SrtGenerator.IsSupportedInput(p));
            if (first is not null) SetStandaloneSrtInputFile(first);
        };
        foreach (Control child in root.Controls)
            EnableStandaloneSrtDrop(child);
    }

    private void ChooseStandaloneSrtInput()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Chọn 1 tệp MP3/Audio hoặc 1 tệp Video để tạo phụ đề .SRT",
            Multiselect = false,
            Filter = "MP3 & Video|*.mp3;*.wav;*.m4a;*.flac;*.mp4;*.mkv;*.mov;*.avi;*.webm;*.ts;*.wmv|Âm thanh (MP3/WAV/M4A)|*.mp3;*.wav;*.m4a;*.flac|Video (MP4/MKV/MOV/AVI)|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.ts;*.wmv|Tất cả tệp|*.*"
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
            SetStandaloneSrtInputFile(picker.FileName);
    }

    private void SetStandaloneSrtInputFile(string file)
    {
        var full = Path.GetFullPath(file);
        _genSrtInputPath.Text = full;
        _genSrtOutputPath.Text = Path.ChangeExtension(full, ".srt");
        var isVideo = SrtGenerator.IsVideoFile(full);
        _genSrtFlowHint.Text = isVideo
            ? "Luồng: Video ➔ Tách MP3 chuẩn ➔ Xuất 1 file .SRT khớp thời gian thực"
            : "Luồng: MP3 ➔ Quét sóng âm & giọng nói ➔ Xuất 1 file .SRT khớp thời gian thực";
        _genSrtFlowHint.ForeColor = isVideo ? AccentWarning : AccentSuccess;
    }

    private void ChooseStandaloneSrtOutput()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Chọn nơi lưu tệp .SRT",
            Filter = "Phụ đề SRT|*.srt",
            FileName = string.IsNullOrWhiteSpace(_genSrtOutputPath.Text)
                ? "phude.srt"
                : Path.GetFileName(_genSrtOutputPath.Text)
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _genSrtOutputPath.Text = dialog.FileName;
    }

    private async Task RunStandaloneSrtGenerationAsync()
    {
        if (_rendering) return;
        var input = _genSrtInputPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
        {
            MessageBox.Show(this, "Hãy chọn 1 tệp MP3 hoặc 1 tệp Video trước khi tạo phụ đề .SRT.",
                "Chưa chọn tệp đầu vào", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var output = string.IsNullOrWhiteSpace(_genSrtOutputPath.Text)
            ? Path.ChangeExtension(input, ".srt")
            : _genSrtOutputPath.Text.Trim();
        _genSrtOutputPath.Text = output;

        var mode = _genSrtMode.SelectedIndex switch
        {
            1 => SrtGenerationMode.AlignProvidedScript,
            2 => SrtGenerationMode.TimecodeTemplateOnly,
            _ => SrtGenerationMode.SpeechToTextAuto
        };

        var langRaw = _genSrtLang.SelectedItem?.ToString() ?? "vi-VN";
        var langCode = langRaw.Split(' ')[0].Trim();

        // Nếu người dùng đang hiển thị nội dung SRT cũ trong ô, không coi đó là kịch bản đầu vào trừ khi chọn chế độ 2
        var scriptText = _genSrtScriptBox.Text.Contains("-->", StringComparison.Ordinal) && mode != SrtGenerationMode.AlignProvidedScript
            ? null
            : _genSrtScriptBox.Text;

        try
        {
            var tools = FfmpegLocator.Find();
            _cts = new CancellationTokenSource();
            SetBusy(true);
            _progress.Value = 0;
            _previousStage = "";

            var reporter = new Progress<RenderProgress>(p =>
            {
                if (IsDisposed) return;
                if (!string.Equals(_previousStage, p.Stage, StringComparison.Ordinal))
                {
                    _previousStage = p.Stage;
                    _status.Text = p.Stage;
                }
                if (p.Percent is double percent)
                {
                    var nextVal = Math.Clamp((int)Math.Round(percent * 10), 0, _progress.Maximum);
                    if (_progress.Value != nextVal)
                        _progress.Value = nextVal;
                }
            });

            var genOptions = new SrtGenerationOptions(
                input,
                output,
                mode,
                langCode,
                scriptText,
                _genKeepVideoMp3.Checked);

            var result = await new SrtGenerator(tools).GenerateAsync(genOptions, reporter, _cts.Token);
            _lastGeneratedSrtPath = result.SrtFilePath;
            _lastExtractedMp3Path = result.ExtractedMp3Path;

            if (!IsDisposed)
            {
                _genSrtScriptBox.Text = await File.ReadAllTextAsync(result.SrtFilePath);
                _genSrtSaveEditsBtn.Enabled = true;
                _genSrtPushToRenderBtn.Enabled = true;
                _genSrtOpenFileBtn.Enabled = true;

                var mp3Note = !string.IsNullOrWhiteSpace(result.ExtractedMp3Path) && File.Exists(result.ExtractedMp3Path)
                    ? $"\n\n• File MP3 tách từ Video:\n{result.ExtractedMp3Path}"
                    : "";

                _status.Text = $"Đã tạo xong SRT ({result.Cues.Count} câu, {TimeSpan.FromSeconds(result.TotalDurationSeconds):mm\\:ss}): {Path.GetFileName(result.SrtFilePath)}";
                MessageBox.Show(this,
                    $"Đã xuất thành công 1 file .SRT khớp thời gian thực ({result.Cues.Count} phân đoạn):\n\n" +
                    $"• File SRT:\n{result.SrtFilePath}{mp3Note}\n\n" +
                    "Bạn có thể sửa trực tiếp trên màn hình hoặc bấm '➕ ĐƯA SANG RENDER VIDEO' để chuyển qua màn hình dựng video.",
                    "Tạo phụ đề .SRT hoàn tất",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Đã hủy tác vụ tạo phụ đề .SRT.";
        }
        catch (Exception ex)
        {
            _status.Text = "Tạo phụ đề .SRT thất bại.";
            MessageBox.Show(this, ex.Message, "Lỗi tạo .SRT", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            var hasFile = !string.IsNullOrWhiteSpace(_lastGeneratedSrtPath) && File.Exists(_lastGeneratedSrtPath);
            _genSrtSaveEditsBtn.Enabled = hasFile;
            _genSrtPushToRenderBtn.Enabled = hasFile;
            _genSrtOpenFileBtn.Enabled = hasFile;
            if (_closeAfterCancel && !IsDisposed)
            {
                _closeAfterCancel = false;
                Close();
            }
        }
    }

    private async Task SaveEditedSrtContentAsync()
    {
        var target = !string.IsNullOrWhiteSpace(_genSrtOutputPath.Text)
            ? _genSrtOutputPath.Text.Trim()
            : _lastGeneratedSrtPath;
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            await File.WriteAllTextAsync(target, _genSrtScriptBox.Text, new System.Text.UTF8Encoding(false));
            _lastGeneratedSrtPath = target;
            _status.Text = $"Đã lưu nội dung .SRT vào: {Path.GetFileName(target)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Không thể lưu .SRT", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void PushGeneratedSrtToRenderList()
    {
        if (string.IsNullOrWhiteSpace(_lastGeneratedSrtPath) || !File.Exists(_lastGeneratedSrtPath)) return;
        AddSrtFiles([_lastGeneratedSrtPath]);
        if (!string.IsNullOrWhiteSpace(_lastExtractedMp3Path) && File.Exists(_lastExtractedMp3Path) && _mp3s.Items.Count == 0)
            AddFiles([_lastExtractedMp3Path], isVideo: false);
        else if (!SrtGenerator.IsVideoFile(_genSrtInputPath.Text) && File.Exists(_genSrtInputPath.Text) && _mp3s.Items.Count == 0)
            AddFiles([_genSrtInputPath.Text], isVideo: false);
        if (SrtGenerator.IsVideoFile(_genSrtInputPath.Text) && File.Exists(_genSrtInputPath.Text) && _videos.Items.Count == 0)
            AddFiles([_genSrtInputPath.Text], isVideo: true);
        SwitchScreen(isRenderScreen: true);
        _status.Text = $"Đã nạp {Path.GetFileName(_lastGeneratedSrtPath)} sang màn hình Render Video.";
    }

    private void OpenGeneratedSrtFile()
    {
        if (string.IsNullOrWhiteSpace(_lastGeneratedSrtPath) || !File.Exists(_lastGeneratedSrtPath)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{_lastGeneratedSrtPath}\"",
            UseShellExecute = true
        });
    }

    private Panel CreateSubtitleCard()
    {
        var card = NewCard();
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        table.Controls.Add(NewLabel("03  PHỤ ĐỀ SRT", 11, Foreground, true), 0, 0);
        var inner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 49));
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 51));
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        _srts.Dock = DockStyle.Fill;
        left.Controls.Add(_srts, 0, 0);
        var fileBtns = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 7, 0, 0) };
        var add = NewButton("+ THÊM SRT", true);
        var remove = NewButton("XÓA CHỌN", false);
        var up = NewButton("↑", false);
        var down = NewButton("↓", false);
        add.Width = 132; remove.Width = 108; up.Width = 40; down.Width = 40;
        add.Click += (_, _) => ChooseSrtFiles();
        remove.Click += (_, _) => RemoveSelectedSrts();
        up.Click += (_, _) => { MoveItem(_srts, -1); UpdateSrtPreview(); };
        down.Click += (_, _) => { MoveItem(_srts, 1); UpdateSrtPreview(); };
        fileBtns.Controls.AddRange([add, remove, up, down]);
        left.Controls.Add(fileBtns, 0, 1);
        inner.Controls.Add(left, 0, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12, 0, 0, 0) };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        options.Controls.Add(NewInlineLabel("Căn theo:", leftMargin: 0));
        _srtMode.Items.AddRange(["1 SRT / toàn bộ audio", "1 SRT / mỗi MP3"]);
        _srtMode.Width = 210; _srtMode.SelectedIndex = 0;
        options.Controls.Add(_srtMode);
        options.Controls.Add(NewInlineLabel("Lệch ms:"));
        _subOffset.BackColor = InputColor; _subOffset.ForeColor = Foreground;
        options.Controls.Add(_subOffset);
        right.Controls.Add(options, 0, 0);
        var style = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        _burnSrt.Appearance = Appearance.Normal;
        _burnSrt.Checked = true; _burnSrt.AutoSize = true;
        _burnSrt.Text = "Chèn chữ vào video"; _burnSrt.ForeColor = Foreground;
        _exportSrt.Checked = true; _exportSrt.AutoSize = true;
        _exportSrt.Text = "Xuất SRT kèm"; _exportSrt.ForeColor = Foreground;
        _burnSrt.Margin = new Padding(0, 8, 18, 0); _exportSrt.Margin = new Padding(0, 8, 0, 0);
        style.Controls.AddRange([_burnSrt, _exportSrt]);
        right.Controls.Add(style, 0, 1);
        _srtPreview.Dock = DockStyle.Fill;
        _srtPreview.Multiline = true;
        _srtPreview.ReadOnly = true;
        _srtPreview.ScrollBars = ScrollBars.Vertical;
        _srtPreview.Text = "Chưa có SRT. Bạn có thể xuất video không phụ đề hoặc tạo mới ở tab Create .SRT.";
        right.Controls.Add(_srtPreview, 0, 2);
        inner.Controls.Add(right, 1, 0);
        table.Controls.Add(inner, 0, 1);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        bottom.Controls.Add(NewInlineLabel("Font chữ:", leftMargin: 0));
        _subFont.Items.AddRange(["Arial", "Segoe UI", "Tahoma", "Verdana", "Times New Roman"]);
        _subFont.SelectedIndex = 0; _subFont.Width = 165;
        bottom.Controls.Add(_subFont);
        bottom.Controls.Add(NewInlineLabel("Cỡ chữ:"));
        bottom.Controls.Add(_subFontSize);
        _srtCount.AutoSize = true; _srtCount.Margin = new Padding(22, 8, 0, 0);
        bottom.Controls.Add(_srtCount);
        table.Controls.Add(bottom, 0, 2);
        card.Controls.Add(table);
        _editableControls.AddRange([_srts, add, remove, up, down, _srtMode, _subOffset,
            _burnSrt, _exportSrt, _subFont, _subFontSize]);
        return card;
    }

    private void ChooseSrtFiles()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Chọn phụ đề SRT", Multiselect = true,
            Filter = "Phụ đề SRT|*.srt|Tất cả tệp|*.*"
        };
        if (picker.ShowDialog(this) == DialogResult.OK) AddSrtFiles(picker.FileNames);
    }

    private void AddSrtFiles(IEnumerable<string> files)
    {
        var exists = new HashSet<string>(_srts.Items.Cast<SubtitleFile>().Select(x => x.FullPath), StringComparer.OrdinalIgnoreCase);
        _srts.BeginUpdate();
        try
        {
            foreach (var file in files)
            {
                if (!File.Exists(file) || !Path.GetExtension(file).Equals(".srt", StringComparison.OrdinalIgnoreCase)) continue;
                var full = Path.GetFullPath(file);
                if (!exists.Add(full)) continue;
                try
                {
                    var cues = SubtitleComposer.Parse(full);
                    var summary = string.Join(Environment.NewLine + Environment.NewLine,
                        cues.Take(3).Select(x => $"{x.Start:hh\\:mm\\:ss\\.fff} → {x.End:hh\\:mm\\:ss\\.fff}  {x.Text.Replace("\n", " / ")}"));
                    var firstCue = cues.FirstOrDefault()?.Text.Replace("\n", " ").Trim() ?? "";
                    _srts.Items.Add(new SubtitleFile(full, cues.Count, summary, firstCue));
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    MessageBox.Show(this, ex.Message, "SRT không hợp lệ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
        finally { _srts.EndUpdate(); }
        SyncSrtImportMode();
        UpdateSrtPreview();
    }

    private void RemoveSelectedSrts()
    {
        if (_srts.SelectedItems.Count == 0) return;
        _srts.BeginUpdate();
        try
        {
            foreach (var obj in _srts.SelectedItems.Cast<object>().ToArray())
                _srts.Items.Remove(obj);
        }
        finally { _srts.EndUpdate(); }
        SyncSrtImportMode();
        UpdateSrtPreview();
    }

    private void SyncSrtImportMode()
    {
        if (_srts.Items.Count > 1 && _srts.Items.Count == _mp3s.Items.Count)
            _srtMode.SelectedIndex = 1;
        else if (_srts.Items.Count == 1 && _mp3s.Items.Count != 1)
            _srtMode.SelectedIndex = 0;
    }

    private void UpdateSrtPreview()
    {
        _srtCount.Text = $"{_srts.Items.Count} file SRT";
        if (_srts.Items.Count == 0)
        {
            _srtPreview.Text = "Chưa có SRT. Có thể xuất video không phụ đề hoặc tạo tự động ở tab Create .SRT.";
            RefreshLayoutPreview();
            return;
        }
        var file = _srts.SelectedItem as SubtitleFile ?? (SubtitleFile)_srts.Items[0];
        _srtPreview.Text = file.PreviewSummary;
        RefreshLayoutPreview();
    }

    private Panel CreateMediaCard(string headline, ListBox list, Label count, bool isVideo)
    {
        var panel = NewCard();
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));

        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        heading.Controls.Add(NewLabel(headline, 11, Foreground, true), 0, 0);
        count.TextAlign = ContentAlignment.MiddleRight;
        count.Dock = DockStyle.Fill;
        heading.Controls.Add(count, 1, 0);
        table.Controls.Add(heading, 0, 0);
        list.Dock = DockStyle.Fill;
        table.Controls.Add(list, 0, 1);

        var buttonGroup = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        var add = NewButton(isVideo ? "+  THÊM VIDEO" : "+  THÊM MP3", true);
        var remove = NewButton("XÓA CHỌN", false);
        var up = NewButton("↑", false);
        var down = NewButton("↓", false);
        add.Width = isVideo ? 146 : 136;
        remove.Width = 108;
        up.Width = 42;
        down.Width = 42;
        add.Click += (_, _) => ChooseFiles(isVideo);
        remove.Click += (_, _) => RemoveSelected(isVideo);
        up.Click += (_, _) => MoveItem(list, -1);
        down.Click += (_, _) => MoveItem(list, 1);
        buttonGroup.Controls.AddRange([add, remove, up, down]);
        table.Controls.Add(buttonGroup, 0, 2);
        if (isVideo)
            table.Controls.Add(CreateVideoSpeedRow(), 0, 3);
        else
        {
            var note = NewLabel("Toàn bộ MP3 được chuẩn hóa song song đa luồng và nối thành 1 audio liền mạch.", 9.5f, Muted);
            note.Padding = new Padding(0, 8, 0, 0);
            table.Controls.Add(note, 0, 3);
        }
        panel.Controls.Add(table);
        _editableControls.AddRange([add, remove, up, down, list]);
        return panel;
    }

    private Control CreateVideoSpeedRow()
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = true,
            Padding = new Padding(0, 6, 0, 0), AutoScroll = false
        };
        var caption = NewInlineLabel("Tốc độ:", leftMargin: 0);
        caption.Margin = new Padding(0, 8, 6, 0);
        row.Controls.Add(caption);
        _selectedVideoSpeed.Items.AddRange(PlaybackSpeeds.Allowed
            .Select(x => (object)new SpeedOption(x)).ToArray());
        _selectedVideoSpeed.SelectedIndex = 3; // 1× by default
        _selectedVideoSpeed.Width = 76;
        _applySpeed.Width = 96;
        _applySpeed.Height = 33;
        row.Controls.Add(_selectedVideoSpeed);
        row.Controls.Add(_applySpeed);

        _muteSourceAudio.Appearance = Appearance.Normal;
        _muteSourceAudio.Checked = true;
        _muteSourceAudio.AutoSize = true;
        _muteSourceAudio.Cursor = Cursors.Hand;
        _muteSourceAudio.Margin = new Padding(8, 7, 0, 0);
        UpdateAudioButton();
        row.Controls.Add(_muteSourceAudio);
        _editableControls.AddRange([_selectedVideoSpeed, _applySpeed, _muteSourceAudio]);
        return row;
    }

    private void UpdateAudioButton()
    {
        _muteSourceAudio.Text = _muteSourceAudio.Checked
            ? "Tắt âm gốc video"
            : "Giữ âm gốc (20%)";
        _muteSourceAudio.ForeColor = _muteSourceAudio.Checked ? Muted : AccentSuccess;
    }

    private void ApplySpeedToSelection()
    {
        if (_videos.SelectedIndices.Count == 0)
        {
            MessageBox.Show(this, "Hãy chọn một hoặc nhiều video trong danh sách trước khi áp dụng tốc độ.",
                "Chưa chọn video", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_selectedVideoSpeed.SelectedItem is not SpeedOption speed) return;
        var indices = _videos.SelectedIndices.Cast<int>().ToArray();
        _videos.BeginUpdate();
        try
        {
            foreach (var index in indices)
            {
                var source = (MediaFile)_videos.Items[index];
                _videos.Items[index] = source with { PlaybackSpeed = speed.Value };
            }
            _videos.ClearSelected();
            foreach (var index in indices) _videos.SetSelected(index, true);
        }
        finally { _videos.EndUpdate(); }
    }

    private Panel CreateOverlayCard(OverlaySlot slot)
    {
        var panel = NewCard();
        panel.Padding = new Padding(14, 12, 14, 12);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        slot.Thumbnail.Dock = DockStyle.Fill;
        slot.Thumbnail.Cursor = Cursors.Hand;
        slot.Thumbnail.Margin = new Padding(0, 0, 10, 0);
        _tooltip.SetToolTip(slot.Thumbnail, "Kéo PNG/JPG/WebP vào ô này: " + slot.Title);
        layout.Controls.Add(slot.Thumbnail, 0, 0);

        var controls = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6,
            Padding = Padding.Empty, Margin = Padding.Empty
        };
        controls.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        controls.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        controls.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        controls.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        controls.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        controls.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        controls.Controls.Add(NewLabel(slot.Title.Replace("04-", "04 "), 11, Foreground, true), 0, 0);
        slot.PathBox.Dock = DockStyle.Fill;
        slot.PathBox.Margin = new Padding(0, 2, 0, 7);
        slot.PathBox.ReadOnly = true;
        slot.PathBox.PlaceholderText = "Chọn ảnh hoặc kéo thả tại đây";
        controls.Controls.Add(slot.PathBox, 0, 1);

        var buttonRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        var browse = NewButton("CHỌN ẢNH", true);
        var clear = NewButton("BỎ ẢNH", false);
        browse.Dock = DockStyle.Fill; browse.Margin = new Padding(0, 0, 7, 7);
        clear.Dock = DockStyle.Fill; clear.Margin = new Padding(0, 0, 0, 7);
        browse.Click += (_, _) => ChooseOverlayImage(slot);
        clear.Click += (_, _) => ClearOverlayImage(slot);
        buttonRow.Controls.Add(browse, 0, 0);
        buttonRow.Controls.Add(clear, 1, 0);
        controls.Controls.Add(buttonRow, 0, 2);

        var sizeRow = CreateOverlayNumberRow("Rộng %", slot.WidthBox, "Cao %", slot.HeightBox);
        controls.Controls.Add(sizeRow, 0, 3);
        var positionRow = CreateOverlayNumberRow("Ngang %", slot.OffsetXBox, "Dọc %", slot.OffsetYBox);
        controls.Controls.Add(positionRow, 0, 4);

        var hint = NewLabel("Vị trí được tính từ góc neo và luôn giữ ảnh trong khung hình.", 8.5f, Muted);
        hint.AutoEllipsis = true;
        hint.Dock = DockStyle.Fill;
        controls.Controls.Add(hint, 0, 5);
        layout.Controls.Add(controls, 1, 0);
        panel.Controls.Add(layout);

        foreach (var number in new[] { slot.WidthBox, slot.HeightBox, slot.OffsetXBox, slot.OffsetYBox })
        {
            number.ValueChanged += (_, _) => RefreshLayoutPreview();
            number.Margin = new Padding(2, 4, 5, 4);
        }
        _tooltip.SetToolTip(slot.WidthBox, "Rộng tối đa từ 20% tới 100% khung video, giữ tỷ lệ ảnh gốc.");
        _tooltip.SetToolTip(slot.HeightBox, "Cao tối đa từ 20% tới 100% khung video, giữ tỷ lệ ảnh gốc.");
        _tooltip.SetToolTip(slot.OffsetXBox, "Di chuyển ngang (0–100%) từ mép của góc chọn; không thể tràn khỏi video.");
        _tooltip.SetToolTip(slot.OffsetYBox, "Di chuyển dọc (0–100%) từ mép của góc chọn; không thể tràn khỏi video.");
        EnableOverlayDrop(panel, slot);
        _editableControls.AddRange([browse, clear, slot.WidthBox, slot.HeightBox, slot.OffsetXBox, slot.OffsetYBox]);
        return panel;
    }

    private static TableLayoutPanel CreateOverlayNumberRow(string label1, NumericUpDown value1,
        string label2, NumericUpDown value2)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23));
        var name1 = NewLabel(label1, 9, Muted);
        var name2 = NewLabel(label2, 9, Muted);
        name1.AutoEllipsis = true;
        name2.AutoEllipsis = true;
        name1.Margin = Padding.Empty; name2.Margin = Padding.Empty;
        value1.Dock = DockStyle.Fill;
        value2.Dock = DockStyle.Fill;
        value1.Width = 60; value2.Width = 60;
        row.Controls.Add(name1, 0, 0);
        row.Controls.Add(value1, 1, 0);
        row.Controls.Add(name2, 2, 0);
        row.Controls.Add(value2, 3, 0);
        return row;
    }

    private static bool TryGetImageFromDrop(IDataObject? data, out string file)
    {
        file = "";
        if (data?.GetData(DataFormats.FileDrop) is not string[] paths) return false;
        var valid = paths.FirstOrDefault(p => File.Exists(p) && IsSupportedOverlayImage(p));
        if (valid is null) return false;
        file = valid;
        return true;
    }

    private static bool IsSupportedOverlayImage(string path) =>
        new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp" }
            .Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void EnableOverlayDrop(Control control, OverlaySlot slot)
    {
        control.AllowDrop = true;
        control.DragEnter += (sender, e) => e.Effect = !_rendering && TryGetImageFromDrop(e.Data, out _)
            ? DragDropEffects.Copy : DragDropEffects.None;
        control.DragDrop += (_, e) =>
        {
            if (_rendering || !TryGetImageFromDrop(e.Data, out var file)) return;
            SetOverlayImage(slot, file);
        };
        foreach (Control child in control.Controls)
            EnableOverlayDrop(child, slot);
    }

    private void SetOverlayImage(OverlaySlot slot, string file)
    {
        if (!File.Exists(file) || !IsSupportedOverlayImage(file)) return;
        slot.PathBox.Text = Path.GetFullPath(file);
        LoadPreview(file, slot.Thumbnail);
        RefreshLayoutPreview();
        _status.Text = $"Đã đặt {Path.GetFileName(file)} vào {slot.Title}.";
    }

    private void ChooseOverlayImage(OverlaySlot slot)
    {
        using var picker = new OpenFileDialog
        {
            Title = $"Chọn ảnh cho {slot.Title}",
            Filter = "Ảnh|*.png;*.jpg;*.jpeg;*.bmp;*.webp|Tất cả tệp|*.*"
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
        {
            if (IsSupportedOverlayImage(picker.FileName)) SetOverlayImage(slot, picker.FileName);
            else MessageBox.Show(this, "Chỉ hỗ trợ PNG, JPG, JPEG, BMP, WebP.", "Định dạng ảnh", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ClearOverlayImage(OverlaySlot slot)
    {
        ClearImage(slot.PathBox, slot.Thumbnail);
        RefreshLayoutPreview();
    }

    private Panel CreateLayoutPreviewCard()
    {
        var panel = NewCard();
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        _layoutPreview.Dock = DockStyle.Fill;
        _layoutPreview.AllowDrop = true;
        _layoutPreview.Margin = new Padding(0, 0, 14, 0);
        _tooltip.SetToolTip(_layoutPreview, "Thả ảnh vào từng phần tư khung preview để gán trực tiếp cho góc tương ứng.");
        table.Controls.Add(_layoutPreview, 0, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(4, 0, 0, 0) };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        right.Controls.Add(NewLabel("05  XEM TRƯỚC BỐ CỤC (16:9)", 11, Foreground, true), 0, 0);

        var desc = NewLabel(
            "• Kéo thả ảnh trực tiếp vào 4 góc khung Preview.\n" +
            "• Chỉnh kích thước & tọa độ độc lập từng góc.\n" +
            "• Phụ đề SRT luôn hiển thị ở lớp trên cùng.",
            9.25f, Muted);
        desc.TextAlign = ContentAlignment.TopLeft;
        desc.Padding = new Padding(0, 8, 0, 0);
        right.Controls.Add(desc, 0, 1);

        var refreshRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0), Margin = Padding.Empty };
        var refresh = NewButton("↻  LÀM MỚI PREVIEW", true);
        refresh.Width = 178;
        refresh.Height = 38;
        refresh.Click += (_, _) => RefreshLayoutPreview();
        refreshRow.Controls.Add(refresh);
        right.Controls.Add(refreshRow, 0, 2);
        _editableControls.Add(refresh);

        table.Controls.Add(right, 1, 0);
        panel.Controls.Add(table);
        return panel;
    }

    private Rectangle GetPreviewImageRect()
    {
        var picture = _layoutPreview;
        if (picture.Image is null || picture.ClientSize.Width <= 0 || picture.ClientSize.Height <= 0)
            return Rectangle.Empty;
        var scale = Math.Min((double)picture.ClientSize.Width / picture.Image.Width,
            (double)picture.ClientSize.Height / picture.Image.Height);
        var width = Math.Max(1, (int)Math.Round(picture.Image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(picture.Image.Height * scale));
        return new Rectangle((picture.ClientSize.Width - width) / 2,
            (picture.ClientSize.Height - height) / 2, width, height);
    }

    private void RefreshLayoutPreview()
    {
        try
        {
            var (w, h) = _resolution.SelectedIndex switch
            {
                0 => (1280, 720),
                2 => (3840, 2160),
                _ => (1920, 1080)
            };
            using var bmp = new Bitmap(1280, 720);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(11, 16, 24));
            var stage = new Rectangle(0, 0, bmp.Width, bmp.Height);
            using (var bgBrush = new LinearGradientBrush(stage,
                Color.FromArgb(18, 26, 38), Color.FromArgb(11, 15, 23), 25f))
            {
                g.FillRectangle(bgBrush, stage);
            }
            using (var gridPen = new Pen(Color.FromArgb(24, 255, 255, 255), 1))
            {
                g.DrawLine(gridPen, bmp.Width / 2, 0, bmp.Width / 2, bmp.Height);
                g.DrawLine(gridPen, 0, bmp.Height / 2, bmp.Width, bmp.Height / 2);
            }
            using (var pen = new Pen(Color.FromArgb(51, 65, 85), 2))
                g.DrawRectangle(pen, 1, 1, bmp.Width - 2, bmp.Height - 2);

            using (var labelBrush = new SolidBrush(Color.FromArgb(210, 148, 163, 184)))
            using (var labelFont = new Font("Segoe UI", 13, FontStyle.Bold))
            {
                g.DrawString($"KHUNG HÌNH XUẤT {w}×{h} (16:9)  ·  4 GÓC ĐỘC LẬP", labelFont, labelBrush, 18, 12);
            }

            foreach (var slot in _overlaySlots)
            {
                if (string.IsNullOrWhiteSpace(slot.PathBox.Text))
                    DrawEmptyCornerGuide(g, stage, slot.Corner);
                else
                    DrawOverlay(g, stage, slot);
            }
            DrawSubtitlePreview(g, stage);

            var old = _layoutPreview.Image;
            _layoutPreview.Image = new Bitmap(bmp);
            old?.Dispose();
        }
        catch
        {
            // Preview is best-effort only.
        }
    }

    private static void DrawEmptyCornerGuide(Graphics g, Rectangle stage, Corner corner)
    {
        const int guideWidth = 172, guideHeight = 78, inset = 16;
        var x = corner is Corner.TopLeft or Corner.BottomLeft
            ? stage.Left + inset : stage.Right - guideWidth - inset;
        var y = corner is Corner.TopLeft or Corner.TopRight
            ? stage.Top + 44 : stage.Bottom - guideHeight - inset;
        var bounds = new Rectangle(x, y, guideWidth, guideHeight);
        using var border = new Pen(Color.FromArgb(110, 71, 85, 105), 1.5f) { DashStyle = DashStyle.Dash };
        using var brush = new SolidBrush(Color.FromArgb(160, 148, 163, 184));
        using var font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        g.DrawRectangle(border, bounds);
        var title = corner switch
        {
            Corner.TopLeft => "TRÊN TRÁI",
            Corner.TopRight => "TRÊN PHẢI",
            Corner.BottomLeft => "DƯỚI TRÁI",
            _ => "DƯỚI PHẢI"
        };
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString("+ THẢ ẢNH\n" + title, font, brush, bounds, format);
    }

    private void DrawOverlay(Graphics g, Rectangle stage, OverlaySlot slot)
    {
        var path = slot.PathBox.Text;
        var corner = slot.Corner;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Image? cached = slot.Thumbnail.Image;
            Image? loadedFromDisk = null;
            try
            {
                if (cached is null)
                {
                    if (!File.Exists(path)) return;
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var raw = Image.FromStream(fs);
                    loadedFromDisk = new Bitmap(raw);
                    cached = loadedFromDisk;
                }
                var maxW = Math.Max(1, stage.Width * (int)slot.WidthBox.Value / 100);
                var maxH = Math.Max(1, stage.Height * (int)slot.HeightBox.Value / 100);
                var scale = Math.Min((double)maxW / cached.Width, (double)maxH / cached.Height);
                var width = Math.Clamp((int)Math.Round(cached.Width * scale), 1, stage.Width);
                var height = Math.Clamp((int)Math.Round(cached.Height * scale), 1, stage.Height);
                var offsetX = Math.Max(0, stage.Width * (int)slot.OffsetXBox.Value / 100);
                var offsetY = Math.Max(0, stage.Height * (int)slot.OffsetYBox.Value / 100);
                var x = corner is Corner.TopLeft or Corner.BottomLeft
                    ? stage.Left + Math.Clamp(offsetX, 0, stage.Width - width)
                    : stage.Right - width - Math.Clamp(offsetX, 0, stage.Width - width);
                var y = corner is Corner.TopLeft or Corner.TopRight
                    ? stage.Top + Math.Clamp(offsetY, 0, stage.Height - height)
                    : stage.Bottom - height - Math.Clamp(offsetY, 0, stage.Height - height);
                g.DrawImage(cached, new Rectangle(x, y, width, height));
                using var pen = new Pen(Color.FromArgb(160, 59, 130, 246), 1.5f);
                g.DrawRectangle(pen, x, y, width, height);
            }
            finally
            {
                loadedFromDisk?.Dispose();
            }
        }
        catch { }
    }

    private void DrawSubtitlePreview(Graphics g, Rectangle stage)
    {
        if (!_burnSrt.Checked || _srts.Items.Count == 0) return;
        try
        {
            var text = GetPreviewSubtitleText();
            var fontSize = Math.Max(16, Math.Min(34, (int)_subFontSize.Value));
            var familyName = _subFont.SelectedItem?.ToString() ?? "Arial";
            using var font = new Font(familyName, fontSize, FontStyle.Bold);
            var rect = new RectangleF(stage.Left + 40, stage.Bottom - 115, stage.Width - 80, 70);
            using var bg = new SolidBrush(Color.FromArgb(135, 0, 0, 0));
            g.FillRectangle(bg, Rectangle.Round(rect));
            using var brush = new SolidBrush(Color.White);
            using var outline = new Pen(Color.Black, 4) { LineJoin = LineJoin.Round };
            using var path = new GraphicsPath();
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            path.AddString(text, font.FontFamily, (int)font.Style, g.DpiY * font.Size / 72, rect, format);
            g.DrawPath(outline, path);
            g.FillPath(brush, path);
        }
        catch { }
    }

    private string GetPreviewSubtitleText()
    {
        if (_srts.Items.Count > 0)
        {
            var file = _srts.SelectedItem as SubtitleFile ?? (SubtitleFile)_srts.Items[0];
            if (!string.IsNullOrWhiteSpace(file.FirstCueLine))
                return file.FirstCueLine;
        }
        return "Chuyện này người chưa biết sẽ hiển thị trên cùng";
    }

    private void LoadWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "software-icon.ico");
        if (!File.Exists(iconPath)) return;
        try
        {
            using var fileIcon = new Icon(iconPath);
            Icon = (Icon)fileIcon.Clone();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
        }
    }

    private void TryLoadAppLogo(PictureBox box)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "software-logo-header.png"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "software-logo.png"),
            Path.Combine(AppContext.BaseDirectory, "software-logo.png")
        };
        foreach (var file in candidates)
        {
            if (!File.Exists(file)) continue;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var image = Image.FromStream(fs);
                var old = box.Image;
                box.Image = new Bitmap(image);
                old?.Dispose();
                return;
            }
            catch { }
        }
    }

    private Panel CreateSettingsCard()
    {
        var panel = NewCard();
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        table.Controls.Add(NewLabel("06  THIẾT LẬP XUẤT VIDEO", 11, Foreground, true), 0, 0);

        var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        var folderLabel = NewLabel("Thư mục xuất:", 10, Muted);
        folderLabel.Dock = DockStyle.Fill;
        folderLabel.TextAlign = ContentAlignment.MiddleLeft;
        folderRow.Controls.Add(folderLabel, 0, 0);
        _folderPath.Dock = DockStyle.Fill;
        folderRow.Controls.Add(_folderPath, 1, 0);
        var browse = NewButton("CHỌN THƯ MỤC", false);
        browse.Dock = DockStyle.Fill;
        browse.Margin = new Padding(8, 0, 0, 3);
        browse.Click += (_, _) => ChooseFolder();
        folderRow.Controls.Add(browse, 2, 0);
        table.Controls.Add(folderRow, 0, 1);

        var optionsRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = true,
            Padding = new Padding(0, 8, 0, 0), AutoScroll = true
        };
        _resolution.Items.AddRange(["720p (1280 × 720)", "1080p (1920 × 1080)", "4K (3840 × 2160)"]);
        _resolution.SelectedIndex = 1;
        _resolution.Width = 180;
        _encoder.Items.AddRange(["H.264 Đa luồng CPU (ổn định)", "NVIDIA NVENC GPU (siêu tốc)"]);
        _encoder.SelectedIndex = 0;
        _encoder.Width = 220;
        optionsRow.Controls.Add(NewInlineLabel("Độ phân giải:", leftMargin: 0));
        optionsRow.Controls.Add(_resolution);
        optionsRow.Controls.Add(NewInlineLabel("Bộ mã hóa:"));
        optionsRow.Controls.Add(_encoder);
        table.Controls.Add(optionsRow, 0, 2);

        var autoLoopNote = NewLabel("Chế độ Ổn định: nhẹ máy, tiết kiệm RAM • Chế độ NV (Siêu tốc): huy động 80–90% CPU & RAM + Lanczos HQ cho tốc độ & chất lượng tối đa.", 9.5f, Muted);
        autoLoopNote.AutoSize = true;
        autoLoopNote.Padding = new Padding(0, 4, 0, 0);
        table.Controls.Add(autoLoopNote, 0, 3);
        _tooltip.SetToolTip(_encoder, "H.264 Đa luồng CPU (ổn định): tiết kiệm RAM, không lag máy.\nNVIDIA NVENC GPU (siêu tốc): sử dụng 80–90% CPU & bộ đệm RAM lớn + nội suy Lanczos HQ để render cực nhanh với chất lượng cao nhất.");
        panel.Controls.Add(table);
        _editableControls.AddRange([_folderPath, browse, _resolution, _encoder]);
        return panel;
    }

    private void WireEvents()
    {
        _render.Click += async (_, _) => await StartRenderAsync();
        _applySpeed.Click += (_, _) => ApplySpeedToSelection();
        _muteSourceAudio.CheckedChanged += (_, _) => UpdateAudioButton();
        _videos.SelectedIndexChanged += (_, _) => SyncSpeedSelection();
        _cancel.Click += (_, _) => _cts?.Cancel();
        _openFolder.Click += (_, _) => OpenFolder();
        ConfigureDragDrop(_videos, true);
        ConfigureDragDrop(_mp3s, false);
        ConfigureListShortcuts(_videos, () => RemoveSelected(true), () => MoveItem(_videos, -1), () => MoveItem(_videos, 1));
        ConfigureListShortcuts(_mp3s, () => RemoveSelected(false), () => MoveItem(_mp3s, -1), () => MoveItem(_mp3s, 1));
        ConfigureListShortcuts(_srts, RemoveSelectedSrts, () => { MoveItem(_srts, -1); UpdateSrtPreview(); }, () => { MoveItem(_srts, 1); UpdateSrtPreview(); });
        _srts.SelectedIndexChanged += (_, _) => UpdateSrtPreview();
        _srts.AllowDrop = true;
        _srts.DragEnter += (_, e) => e.Effect = !_rendering && e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        _srts.DragDrop += (_, e) =>
        {
            if (!_rendering && e.Data?.GetData(DataFormats.FileDrop) is string[] files) AddSrtFiles(files);
        };
        _tooltip.SetToolTip(_srts, "Kéo thả SRT. Phím Delete để xóa, Ctrl+A chọn hết, Alt+↑/↓ để đổi thứ tự.");
        _tooltip.SetToolTip(_subOffset, "Dịch tất cả phụ đề sớm/muộn theo mili giây, ví dụ -300 ms.");
        _tooltip.SetToolTip(_videos, "Kéo thả video. Chọn 1 hoặc nhiều dòng, chọn tốc độ rồi nhấn ÁP DỤNG; phím Delete để xóa.");
        _tooltip.SetToolTip(_selectedVideoSpeed, "0.4× = 2.5 lần thời lượng; 0.5× = 2 lần; 2× = còn một nửa.");
        _tooltip.SetToolTip(_muteSourceAudio, "Mặc định tắt tiếng gốc. Bỏ chọn để giữ âm gốc ở 20% trộn cùng MP3.");
        _tooltip.SetToolTip(_mp3s, "Kéo thả nhiều MP3. Chuẩn hóa song song đa luồng và nối thành một audio duy nhất.");
        _layoutPreview.DragEnter += (sender, e) =>
            e.Effect = !_rendering && TryGetImageFromDrop(e.Data, out _)
                ? DragDropEffects.Copy : DragDropEffects.None;
        _layoutPreview.DragDrop += (_, e) =>
        {
            if (_rendering || !TryGetImageFromDrop(e.Data, out var file)) return;
            var clientPoint = _layoutPreview.PointToClient(new Point(e.X, e.Y));
            var imageRect = GetPreviewImageRect();
            if (imageRect.Width <= 0 || !imageRect.Contains(clientPoint)) return;
            var left = clientPoint.X < imageRect.Left + imageRect.Width / 2;
            var top = clientPoint.Y < imageRect.Top + imageRect.Height / 2;
            var corner = top
                ? (left ? Corner.TopLeft : Corner.TopRight)
                : (left ? Corner.BottomLeft : Corner.BottomRight);
            var slot = _overlaySlots.First(x => x.Corner == corner);
            SetOverlayImage(slot, file);
        };
        _resolution.SelectedIndexChanged += (_, _) => RefreshLayoutPreview();
        _burnSrt.CheckedChanged += (_, _) => RefreshLayoutPreview();
        _subFont.SelectedIndexChanged += (_, _) => RefreshLayoutPreview();
        _subFontSize.ValueChanged += (_, _) => RefreshLayoutPreview();
        _srtMode.SelectedIndexChanged += (_, _) => RefreshLayoutPreview();
        FormClosing += OnFormClosing;
        RefreshLayoutPreview();
    }

    private void SyncSpeedSelection()
    {
        if (_videos.SelectedItems.Count != 1) return;
        var selected = (MediaFile)_videos.SelectedItem!;
        for (var i = 0; i < _selectedVideoSpeed.Items.Count; i++)
        {
            if (_selectedVideoSpeed.Items[i] is SpeedOption opt &&
                Math.Abs(opt.Value - selected.PlaybackSpeed) < 0.000001)
            {
                _selectedVideoSpeed.SelectedIndex = i;
                return;
            }
        }
    }

    private void CheckFfmpeg()
    {
        try
        {
            var found = FfmpegLocator.Find();
            _ffmpegStatus.Text = "● FFmpeg & FFprobe sẵn sàng";
            _ffmpegStatus.ForeColor = AccentSuccess;
            _tooltip.SetToolTip(_ffmpegStatus, found.Ffmpeg);
        }
        catch (FileNotFoundException)
        {
            _ffmpegStatus.Text = "⚠ Chưa tìm thấy FFmpeg (xem README)";
            _ffmpegStatus.ForeColor = AccentWarning;
        }
    }

    private void ChooseFiles(bool isVideo)
    {
        using var picker = new OpenFileDialog
        {
            Title = isVideo ? "Chọn nhiều video" : "Chọn nhiều MP3",
            Multiselect = true,
            Filter = isVideo
                ? "Video|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.wmv|Tất cả tệp|*.*"
                : "Âm thanh MP3|*.mp3|Tất cả tệp|*.*"
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
            AddFiles(picker.FileNames, isVideo);
    }

    private void AddFiles(IEnumerable<string> paths, bool isVideo)
    {
        var list = isVideo ? _videos : _mp3s;
        var allowed = isVideo
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".ts", ".wmv" }
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3" };
        var existing = new HashSet<string>(list.Items.Cast<MediaFile>().Select(x => x.FullPath), StringComparer.OrdinalIgnoreCase);
        list.BeginUpdate();
        try
        {
            foreach (var file in paths)
            {
                if (!File.Exists(file) || !allowed.Contains(Path.GetExtension(file))) continue;
                var full = Path.GetFullPath(file);
                if (existing.Add(full)) list.Items.Add(new MediaFile(full, isVideo));
            }
        }
        finally { list.EndUpdate(); }
        if (!isVideo) SyncSrtImportMode();
        UpdateCounts();
        RefreshLayoutPreview();
    }

    private void RemoveSelected(bool isVideo)
    {
        var list = isVideo ? _videos : _mp3s;
        if (list.SelectedItems.Count == 0) return;
        list.BeginUpdate();
        try
        {
            foreach (var item in list.SelectedItems.Cast<object>().ToArray())
                list.Items.Remove(item);
        }
        finally { list.EndUpdate(); }
        if (!isVideo) SyncSrtImportMode();
        UpdateCounts();
        RefreshLayoutPreview();
    }

    private static void MoveItem(ListBox list, int direction)
    {
        if (list.SelectedIndices.Count != 1) return;
        var index = list.SelectedIndex;
        var next = index + direction;
        if (next < 0 || next >= list.Items.Count) return;
        var current = list.Items[index];
        list.Items.RemoveAt(index);
        list.Items.Insert(next, current);
        list.SelectedIndex = next;
    }

    private void ConfigureDragDrop(ListBox list, bool isVideo)
    {
        list.AllowDrop = true;
        list.DragEnter += (_, e) =>
            e.Effect = !_rendering && e.Data?.GetDataPresent(DataFormats.FileDrop) == true
                ? DragDropEffects.Copy : DragDropEffects.None;
        list.DragDrop += (_, e) =>
        {
            if (!_rendering && e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
                AddFiles(paths, isVideo);
        };
    }

    private void ConfigureListShortcuts(ListBox list, Action onDelete, Action onMoveUp, Action onMoveDown)
    {
        list.KeyDown += (_, e) =>
        {
            if (_rendering) return;
            if (e.KeyCode == Keys.Delete)
            {
                onDelete();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                list.BeginUpdate();
                try
                {
                    for (var i = 0; i < list.Items.Count; i++)
                        list.SetSelected(i, true);
                }
                finally { list.EndUpdate(); }
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.Alt && e.KeyCode == Keys.Up)
            {
                onMoveUp();
                e.Handled = true;
            }
            else if (e.Alt && e.KeyCode == Keys.Down)
            {
                onMoveDown();
                e.Handled = true;
            }
        };
    }

    private static void ClearImage(TextBox field, PictureBox preview)
    {
        field.Text = "";
        var old = preview.Image;
        preview.Image = null;
        old?.Dispose();
    }

    private static void LoadPreview(string file, PictureBox preview)
    {
        var old = preview.Image;
        preview.Image = null;
        old?.Dispose();
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var image = Image.FromStream(fs);
            preview.Image = new Bitmap(image);
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or IOException)
        {
        }
    }

    private void ChooseFolder()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Chọn thư mục lưu tệp MP4 duy nhất",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_folderPath.Text) ? _folderPath.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
            _folderPath.Text = picker.SelectedPath;
    }

    private RenderOptions CollectOptions()
    {
        if (_videos.Items.Count == 0) throw new ArgumentException("Bạn cần chọn ít nhất một video.");
        if (_mp3s.Items.Count == 0) throw new ArgumentException("Bạn cần chọn ít nhất một MP3.");

        var videos = _videos.Items.Cast<MediaFile>()
            .Select(x => new VideoClipOption(x.FullPath, x.PlaybackSpeed)).ToArray();
        var mp3s = _mp3s.Items.Cast<MediaFile>().Select(x => x.FullPath).ToArray();
        var overlays = new List<OverlayOption>();
        foreach (var slot in _overlaySlots)
            if (!string.IsNullOrWhiteSpace(slot.PathBox.Text))
                overlays.Add(new OverlayOption(slot.PathBox.Text, slot.Corner, (int)slot.WidthBox.Value, (int)slot.HeightBox.Value, (int)slot.OffsetXBox.Value, (int)slot.OffsetYBox.Value));

        var (w, h) = _resolution.SelectedIndex switch
        {
            0 => (1280, 720),
            2 => (3840, 2160),
            _ => (1920, 1080)
        };
        SubtitleOptions? subtitles = null;
        if (_srts.Items.Count > 0)
            subtitles = new SubtitleOptions(
                _srts.Items.Cast<SubtitleFile>().Select(x => x.FullPath).ToArray(),
                _srtMode.SelectedIndex == 1 ? SubtitleImportMode.OneSrtPerMp3 : SubtitleImportMode.GlobalTimeline,
                _burnSrt.Checked, _exportSrt.Checked, (int)_subOffset.Value,
                _subFont.SelectedItem?.ToString() ?? "Arial", (int)_subFontSize.Value);
        return new RenderOptions(videos, mp3s, overlays, _folderPath.Text.Trim(),
            w, h, 30, _muteSourceAudio.Checked,
            _encoder.SelectedIndex == 1 ? VideoEncoder.Nvenc : VideoEncoder.X264, subtitles);
    }

    private async Task StartRenderAsync()
    {
        if (_rendering) return;
        try
        {
            var options = CollectOptions();
            var tools = FfmpegLocator.Find();
            _cts = new CancellationTokenSource();
            SetBusy(true);
            _progress.Value = 0;
            _previousStage = "";
            var reporter = new Progress<RenderProgress>(p =>
            {
                if (IsDisposed) return;
                if (!string.Equals(_previousStage, p.Stage, StringComparison.Ordinal))
                {
                    _previousStage = p.Stage;
                    _status.Text = p.Stage;
                }
                if (p.Percent is double percent)
                {
                    var nextVal = Math.Clamp((int)Math.Round(percent * 10), 0, _progress.Maximum);
                    if (_progress.Value != nextVal)
                        _progress.Value = nextVal;
                }
            });
            var output = await new RenderEngine(tools).RenderAsync(options, reporter, _cts.Token);
            if (!IsDisposed)
            {
                _status.Text = $"Đã xuất 1 file MP4: {Path.GetFileName(output)}";
                MessageBox.Show(this, "Đã xuất 1 video duy nhất:\n\n" +
                    output + (File.Exists(Path.ChangeExtension(output, ".srt"))
                        ? "\n\nKèm file SRT đồng bộ: " + Path.ChangeExtension(output, ".srt") : ""),
                    "Hoàn thành", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Đã hủy. Không có MP4 chưa hoàn chỉnh trong thư mục đầu ra.";
        }
        catch (Exception ex)
        {
            _status.Text = "Xuất video thất bại. Xem chi tiết thông báo lỗi.";
            MessageBox.Show(this, ex.Message, "Không thể xuất video", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            if (_closeAfterCancel && !IsDisposed)
            {
                _closeAfterCancel = false;
                Close();
            }
        }
    }

    private void SetBusy(bool busy)
    {
        _rendering = busy;
        foreach (var c in _editableControls) c.Enabled = !busy;
        _render.Enabled = !busy;
        _cancel.Enabled = busy;
    }

    private void UpdateCounts()
    {
        _videoCount.Text = $"{_videos.Items.Count} video";
        _mp3Count.Text = $"{_mp3s.Items.Count} MP3";
        _srtCount.Text = $"{_srts.Items.Count} SRT";
    }

    private void OpenFolder()
    {
        if (!Directory.Exists(_folderPath.Text))
        {
            MessageBox.Show(this, "Chưa có thư mục đầu ra.", "Thông báo",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _folderPath.Text, UseShellExecute = true
        });
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_rendering)
        {
            e.Cancel = true;
            if (MessageBox.Show(this, "Đang xử lý tác vụ. Hủy và thoát?", "Xác nhận",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                _closeAfterCancel = true;
                _cts?.Cancel();
            }
        }
        else
        {
            foreach (var slot in _overlaySlots)
                slot.Thumbnail.Image?.Dispose();
            _layoutPreview.Image?.Dispose();
            _logoBox?.Image?.Dispose();
            _tooltip.Dispose();
        }
    }

    private static StudioCard NewCard() => new()
    {
        Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 12),
        Margin = new Padding(7), BackColor = CardColor,
        BorderColor = Color.FromArgb(38, 50, 68)
    };

    private static Label NewLabel(string value, float size, Color color, bool bold = false) => new()
    {
        Text = value, AutoSize = false, Dock = DockStyle.Fill,
        UseMnemonic = false,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        ForeColor = color, BackColor = Color.Transparent
    };

    private static Label NewInlineLabel(string text, int leftMargin = 12) => new()
    {
        Text = text, AutoSize = true, UseMnemonic = false,
        ForeColor = Muted, BackColor = Color.Transparent,
        Margin = new Padding(leftMargin, 7, 4, 0)
    };

    private static TextBox NewTextBox() => new()
    {
        BackColor = InputColor, ForeColor = Foreground, BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Segoe UI", 10), Margin = new Padding(0, 2, 3, 3)
    };

    private static ListBox NewListBox() => new()
    {
        BackColor = InputColor, ForeColor = Foreground, BorderStyle = BorderStyle.FixedSingle,
        IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended,
        HorizontalScrollbar = true, ItemHeight = 25,
        Font = new Font("Segoe UI", 10)
    };

    private static PictureBox NewPreview() => new()
    {
        Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.FromArgb(12, 17, 25), Margin = new Padding(0, 0, 8, 0)
    };

    private static ComboBox NewCombo() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
        BackColor = InputColor, ForeColor = Foreground,
        Font = new Font("Segoe UI", 10)
    };

    private static NumericUpDown NewNumber(int value, int min, int max) => new()
    {
        Minimum = min, Maximum = max, Value = value, Width = 57,
        BackColor = InputColor, ForeColor = Foreground,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Segoe UI", 10)
    };

    private static StudioButton NewButton(string text, bool primary) => new()
    {
        Text = text,
        Height = 34,
        Width = 120,
        IsPrimary = primary,
        BackColor = primary ? Accent : Color.FromArgb(30, 41, 59),
        ForeColor = Foreground,
        BorderColor = primary ? Accent : Color.FromArgb(51, 65, 85),
        HoverBackColor = primary ? Color.FromArgb(59, 130, 246) : Color.FromArgb(43, 57, 79),
        PressedBackColor = primary ? Color.FromArgb(29, 78, 216) : Color.FromArgb(23, 32, 46),
        Font = new Font("Segoe UI", 9F, FontStyle.Bold),
        Margin = new Padding(0, 0, 8, 0)
    };

    private sealed record MediaFile(string FullPath, bool IsVideo, double PlaybackSpeed = 1.0)
    {
        public override string ToString() => IsVideo
            ? $"{Path.GetFileName(FullPath)}    [{PlaybackSpeeds.Format(PlaybackSpeed)}]"
            : Path.GetFileName(FullPath);
    }

    private sealed record SubtitleFile(string FullPath, int Cues, string PreviewSummary = "", string FirstCueLine = "")
    {
        public override string ToString() => $"{Path.GetFileName(FullPath)}  •  {Cues} câu";
    }

    private sealed record SpeedOption(double Value)
    {
        public override string ToString() => PlaybackSpeeds.Format(Value);
    }

    private sealed record OverlaySlot(Corner Corner, string Title, TextBox PathBox, PictureBox Thumbnail, NumericUpDown WidthBox, NumericUpDown HeightBox, NumericUpDown OffsetXBox, NumericUpDown OffsetYBox);
}
