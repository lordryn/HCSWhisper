using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HCSWhisper
{
    /// <summary>
    /// Dark-themed HCSWhisper Control Panel & System Tray Icon (v1.1.0).
    /// Tab 1: General, HOTAS/Key PTT & Open-Mic VAD, Whisper Model, Live Recognition Feed.
    /// Tab 2: Customizable Place Nicknames ("ash" -> "Ashland", "babbage" -> "New Babbage") & Command Aliases.
    /// Includes 1-click GitHub Update Checker.
    /// </summary>
    public sealed class SettingsForm : Form
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        private readonly HcsConfig _config;
        private readonly NicknameStore _nicknames;
        private readonly AudioCaptureEngine _audio;
        private readonly Func<string> _getStatusSummary;
        private readonly Func<IReadOnlyList<string>> _getKnownDestinations;
        private readonly Action _onModelChanged;
        private readonly Action _onReloadTriggers;
        private readonly Action _onManualUpdateCheck;

        // Tab switcher buttons & panels
        private Button _tabBtnGeneral;
        private Button _tabBtnNicknames;
        private Panel _pageGeneral;
        private Panel _pageNicknames;

        // General tab controls
        private RadioButton _rbPtt;
        private RadioButton _rbOpenMic;
        private Button _btnBindPtt;
        private Button _btnBindSuppress;
        private Button _btnClearSuppress;
        private ComboBox _cbModel;
        private ComboBox _cbMic;
        private TrackBar _tbVad;
        private Label _lblVadVal;
        private CheckBox _chkBeep;
        private CheckBox _chkAutoMuteVa;
        private CheckBox _chkAutoUpdate;
        private Label _lblStatus;
        private Panel _meterFill;
        private Panel _meterBg;
        private RichTextBox _rtbLog;
        private System.Windows.Forms.Timer _uiTimer;
        private CancellationTokenSource _bindCts;

        // Nicknames tab controls
        private TextBox _txtFilterNicks;
        private ListView _lvNicknames;
        private ComboBox _cbNickKind;
        private TextBox _txtNickSpoken;
        private ComboBox _cbNickTarget;
        private Label _lblNickCount;

        private static readonly Color BgDark = Color.FromArgb(20, 24, 33);
        private static readonly Color CardBg = Color.FromArgb(29, 35, 48);
        private static readonly Color AccentCyan = Color.FromArgb(56, 189, 248);
        private static readonly Color AccentGreen = Color.FromArgb(52, 211, 153);
        private static readonly Color TextPrimary = Color.FromArgb(235, 240, 248);
        private static readonly Color TextMuted = Color.FromArgb(148, 163, 184);

        public SettingsForm(
            HcsConfig config,
            NicknameStore nicknames,
            AudioCaptureEngine audio,
            Func<string> getStatusSummary,
            Func<IReadOnlyList<string>> getKnownDestinations,
            Action onModelChanged,
            Action onReloadTriggers,
            Action onManualUpdateCheck)
        {
            _config = config;
            _nicknames = nicknames;
            _audio = audio;
            _getStatusSummary = getStatusSummary;
            _getKnownDestinations = getKnownDestinations;
            _onModelChanged = onModelChanged;
            _onReloadTriggers = onReloadTriggers;
            _onManualUpdateCheck = onManualUpdateCheck;

            BuildUi();
        }

        public static Icon CreateDynamicAppIcon(bool recording = false)
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var bgBrush = new SolidBrush(recording ? Color.FromArgb(220, 38, 38) : Color.FromArgb(15, 23, 42));
                using var ringPen = new Pen(recording ? Color.FromArgb(252, 165, 165) : Color.FromArgb(56, 189, 248), 2.2f);
                g.FillEllipse(bgBrush, 1, 1, 29, 29);
                g.DrawEllipse(ringPen, 2, 2, 27, 27);

                using var fg = new SolidBrush(Color.White);
                g.FillRoundedRectangleFallback(fg, 12, 7, 8, 12, 4);
                using var arcPen = new Pen(Color.White, 2f);
                g.DrawArc(arcPen, 9, 10, 14, 12, 0, 180);
                g.DrawLine(arcPen, 16, 22, 16, 26);
                g.DrawLine(arcPen, 12, 26, 20, 26);
            }

            IntPtr hIcon = bmp.GetHicon();
            Icon cloned = (Icon)Icon.FromHandle(hIcon).Clone();
            DestroyIcon(hIcon);
            return cloned;
        }

        private void BuildUi()
        {
            Text = $"HCSWhisper v{HcsVersion.Current} — AI Speech Engine for VoiceAttack & HCS Voice Packs";
            Size = new Size(740, 680);
            MinimumSize = new Size(690, 610);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BgDark;
            ForeColor = TextPrimary;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            Icon = CreateDynamicAppIcon(false);

            var outer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14),
                ColumnCount = 1,
                RowCount = 3
            };
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            // 1. Top Status & Actions Bar
            var statusCard = CreateCard(new Padding(12, 9, 12, 9));
            statusCard.Height = 46;
            _lblStatus = new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = AccentCyan,
                Font = new Font("Segoe UI Semibold", 9.25f),
                UseMnemonic = false,
                Text = _getStatusSummary?.Invoke() ?? "Initializing HCSWhisper..."
            };
            var topBtnFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight
            };
            var btnCheckUpdate = CreateFlatButton("Check Updates", 118);
            btnCheckUpdate.Click += (_, _) => _onManualUpdateCheck?.Invoke();
            var btnReload = CreateFlatButton("Reload HCS Pack", 125);
            btnReload.Margin = new Padding(8, 3, 0, 3);
            btnReload.Click += (_, _) =>
            {
                _onReloadTriggers?.Invoke();
                RefreshStatusLabel();
                RefreshDestinationsAutocomplete();
            };
            topBtnFlow.Controls.Add(btnCheckUpdate);
            topBtnFlow.Controls.Add(btnReload);
            statusCard.Controls.Add(_lblStatus);
            statusCard.Controls.Add(topBtnFlow);
            outer.Controls.Add(statusCard, 0, 0);

            // 2. Clean Dark Segmented Tab Bar
            var navBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10),
                FlowDirection = FlowDirection.LeftToRight
            };
            _tabBtnGeneral = CreateFlatButton("General && Live Feed", 185);
            _tabBtnNicknames = CreateFlatButton("Place Nicknames && Aliases", 215);
            _tabBtnNicknames.Margin = new Padding(8, 3, 3, 3);
            _tabBtnGeneral.Click += (_, _) => SwitchPage(showGeneral: true);
            _tabBtnNicknames.Click += (_, _) => SwitchPage(showGeneral: false);
            navBar.Controls.Add(_tabBtnGeneral);
            navBar.Controls.Add(_tabBtnNicknames);
            outer.Controls.Add(navBar, 0, 1);

            // 3. Content Host Panel
            var contentHost = new Panel { Dock = DockStyle.Fill };
            _pageGeneral = BuildGeneralPage();
            _pageNicknames = BuildNicknamesPage();
            contentHost.Controls.Add(_pageGeneral);
            contentHost.Controls.Add(_pageNicknames);
            outer.Controls.Add(contentHost, 0, 2);

            Controls.Add(outer);
            SwitchPage(showGeneral: true);
            UpdateModeVisibility();

            _uiTimer = new System.Windows.Forms.Timer { Interval = 50 };
            _uiTimer.Tick += (_, _) =>
            {
                if (!Visible || !_pageGeneral.Visible || _audio == null) return;
                float level = Math.Clamp(_audio.CurrentRms * 8f, 0f, 1f);
                _meterFill.Width = (int)(_meterBg.Width * level);
                _meterFill.BackColor = _audio.IsRecording ? AccentCyan : AccentGreen;
            };
            _uiTimer.Start();

            FormClosing += (_, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                }
            };
        }

        private Panel BuildGeneralPage()
        {
            var page = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3
            };
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            // Activation & Bindings Card
            var bindCard = CreateCard(new Padding(14, 10, 14, 10));
            var bindLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
            bindLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
            bindLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bindLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));

            var modePanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
            _rbPtt = new RadioButton { Text = "Push-to-Talk (Recommended)", AutoSize = true, Checked = !string.Equals(_config.Mode, "OpenMic", StringComparison.OrdinalIgnoreCase), ForeColor = TextPrimary };
            _rbOpenMic = new RadioButton { Text = "Open-Mic (Auto VAD)", AutoSize = true, Checked = string.Equals(_config.Mode, "OpenMic", StringComparison.OrdinalIgnoreCase), ForeColor = TextPrimary, Margin = new Padding(18, 3, 0, 0) };
            _rbPtt.CheckedChanged += (_, _) => { if (_rbPtt.Checked) { _config.Mode = "PTT"; _config.Save(); UpdateModeVisibility(); } };
            _rbOpenMic.CheckedChanged += (_, _) => { if (_rbOpenMic.Checked) { _config.Mode = "OpenMic"; _config.Save(); UpdateModeVisibility(); } };
            modePanel.Controls.Add(_rbPtt);
            modePanel.Controls.Add(_rbOpenMic);

            bindLayout.Controls.Add(CreateMutedLabel("Listening Mode:"), 0, 0);
            bindLayout.Controls.Add(modePanel, 1, 0);
            bindLayout.SetColumnSpan(modePanel, 2);

            _btnBindPtt = CreateFlatButton($"PTT: {InputPoller.FormatFriendly(_config.PttBinding)}  (Click to Bind)", 280);
            _btnBindPtt.Dock = DockStyle.Fill;
            _btnBindPtt.Click += async (_, _) => await BindInputInteractiveAsync(isPtt: true);
            bindLayout.Controls.Add(CreateMutedLabel("Push-to-Talk Button:"), 0, 1);
            bindLayout.Controls.Add(_btnBindPtt, 1, 1);
            bindLayout.SetColumnSpan(_btnBindPtt, 2);

            _btnBindSuppress = CreateFlatButton($"Mute Key: {InputPoller.FormatFriendly(_config.SuppressBinding)}  (Click to Bind)", 220);
            _btnBindSuppress.Dock = DockStyle.Fill;
            _btnBindSuppress.Click += async (_, _) => await BindInputInteractiveAsync(isPtt: false);
            _btnClearSuppress = CreateFlatButton("Clear", 85);
            _btnClearSuppress.Dock = DockStyle.Fill;
            _btnClearSuppress.Click += (_, _) =>
            {
                _config.SuppressBinding = "";
                _config.Save();
                _btnBindSuppress.Text = "Mute Key: None  (Click to Bind)";
            };
            bindLayout.Controls.Add(CreateMutedLabel("Discord PTT Interlock:"), 0, 2);
            bindLayout.Controls.Add(_btnBindSuppress, 1, 2);
            bindLayout.Controls.Add(_btnClearSuppress, 2, 2);

            var vadFlow = new Panel { Height = 30, Dock = DockStyle.Fill };
            _tbVad = new TrackBar { Minimum = 4, Maximum = 80, Value = Math.Clamp((int)(_config.VadRmsThreshold * 1000), 4, 80), TickStyle = TickStyle.None, Width = 260, Height = 26, Left = 0, Top = 2 };
            _lblVadVal = new Label { AutoSize = true, Left = 270, Top = 5, ForeColor = TextMuted, Text = $"{_tbVad.Value / 10.0:F1}% threshold" };
            _tbVad.Scroll += (_, _) =>
            {
                _config.VadRmsThreshold = _tbVad.Value / 1000f;
                _lblVadVal.Text = $"{_tbVad.Value / 10.0:F1}% threshold";
                _config.Save();
            };
            vadFlow.Controls.Add(_tbVad);
            vadFlow.Controls.Add(_lblVadVal);
            bindLayout.Controls.Add(CreateMutedLabel("Open-Mic Sensitivity:"), 0, 3);
            bindLayout.Controls.Add(vadFlow, 1, 3);
            bindLayout.SetColumnSpan(vadFlow, 2);

            bindCard.Controls.Add(bindLayout);
            bindCard.AutoSize = true;
            page.Controls.Add(bindCard, 0, 0);

            // Audio & AI Model Card
            var modelCard = CreateCard(new Padding(14, 10, 14, 10));
            var modelLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
            modelLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
            modelLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            _cbMic = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgDark, ForeColor = TextPrimary };
            var devices = AudioCaptureEngine.GetCaptureDevices();
            int selMic = 0;
            for (int i = 0; i < devices.Count; i++)
            {
                _cbMic.Items.Add(devices[i]);
                if (string.Equals(devices[i].Id, _config.MicDeviceId ?? "", StringComparison.OrdinalIgnoreCase))
                    selMic = i;
            }
            if (_cbMic.Items.Count > 0) _cbMic.SelectedIndex = selMic;
            _cbMic.SelectedIndexChanged += (_, _) =>
            {
                if (_cbMic.SelectedItem is MicDeviceInfo info)
                {
                    _config.MicDeviceId = info.Id;
                    _config.Save();
                    _audio?.RestartDevice();
                }
            };
            modelLayout.Controls.Add(CreateMutedLabel("Microphone Device:"), 0, 0);
            modelLayout.Controls.Add(_cbMic, 1, 0);

            _meterBg = new Panel { Height = 12, Dock = DockStyle.Fill, BackColor = Color.FromArgb(15, 18, 25), Margin = new Padding(3, 8, 3, 8) };
            _meterFill = new Panel { Height = 12, Width = 0, Left = 0, Top = 0, BackColor = AccentGreen };
            _meterBg.Controls.Add(_meterFill);
            modelLayout.Controls.Add(CreateMutedLabel("Live Mic Level:"), 0, 1);
            modelLayout.Controls.Add(_meterBg, 1, 1);

            _cbModel = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgDark, ForeColor = TextPrimary };
            _cbModel.Items.AddRange(new object[]
            {
                "base.en  — Fast & Accurate (~140 ms, Recommended)",
                "small.en — Extra-High Accuracy (~240 ms)",
                "tiny.en  — Ultra-Light (~85 ms, Low VRAM)"
            });
            _cbModel.SelectedIndex = (_config.ModelName ?? "base.en").ToLowerInvariant() switch
            {
                "small.en" => 1,
                "tiny.en" => 2,
                _ => 0
            };
            _cbModel.SelectedIndexChanged += (_, _) =>
            {
                string picked = _cbModel.SelectedIndex switch
                {
                    1 => "small.en",
                    2 => "tiny.en",
                    _ => "base.en"
                };
                if (!string.Equals(_config.ModelName, picked, StringComparison.OrdinalIgnoreCase))
                {
                    _config.ModelName = picked;
                    _config.Save();
                    _onModelChanged?.Invoke();
                }
            };
            modelLayout.Controls.Add(CreateMutedLabel("Whisper AI Model:"), 0, 2);
            modelLayout.Controls.Add(_cbModel, 1, 2);

            var optsFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
            _chkBeep = new CheckBox { Text = "PTT cue beep", Checked = _config.PlayPttBeep, AutoSize = true, ForeColor = TextMuted };
            _chkBeep.CheckedChanged += (_, _) => { _config.PlayPttBeep = _chkBeep.Checked; _config.Save(); };
            _chkAutoMuteVa = new CheckBox { Text = "Auto-mute VA Windows Speech", Checked = _config.AutoMuteVaSpeech, AutoSize = true, ForeColor = TextMuted, Margin = new Padding(14, 3, 0, 0) };
            _chkAutoMuteVa.CheckedChanged += (_, _) => { _config.AutoMuteVaSpeech = _chkAutoMuteVa.Checked; _config.Save(); };
            _chkAutoUpdate = new CheckBox { Text = "Prompt on updates", Checked = _config.CheckUpdatesOnStartup, AutoSize = true, ForeColor = TextMuted, Margin = new Padding(14, 3, 0, 0) };
            _chkAutoUpdate.CheckedChanged += (_, _) => { _config.CheckUpdatesOnStartup = _chkAutoUpdate.Checked; _config.Save(); };
            optsFlow.Controls.Add(_chkBeep);
            optsFlow.Controls.Add(_chkAutoMuteVa);
            optsFlow.Controls.Add(_chkAutoUpdate);
            modelLayout.Controls.Add(CreateMutedLabel("Options:"), 0, 3);
            modelLayout.Controls.Add(optsFlow, 1, 3);

            modelCard.Controls.Add(modelLayout);
            modelCard.AutoSize = true;
            page.Controls.Add(modelCard, 0, 1);

            // Live Recognition Feed
            var logCard = CreateCard(new Padding(10));
            logCard.Dock = DockStyle.Fill;
            var lblLogHeader = new Label { Text = "Live Recognition & HCS Command Feed", UseMnemonic = false, Dock = DockStyle.Top, Height = 22, ForeColor = TextMuted, Font = new Font("Segoe UI Semibold", 9f) };
            _rtbLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(14, 17, 24),
                ForeColor = TextPrimary,
                Font = new Font("Consolas", 9.5f)
            };
            logCard.Controls.Add(_rtbLog);
            logCard.Controls.Add(lblLogHeader);
            page.Controls.Add(logCard, 0, 2);

            return page;
        }

        private Panel BuildNicknamesPage()
        {
            var page = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3
            };
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // 1. Search / Filter Header Card
            var searchCard = CreateCard(new Padding(12, 8, 12, 8));
            searchCard.AutoSize = true;
            var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            searchRow.Controls.Add(CreateMutedLabel("Filter Nicknames:"), 0, 0);
            _txtFilterNicks = new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = BgDark,
                ForeColor = TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                PlaceholderText = "Type e.g. ash, babbage, l5, grim, kareah..."
            };
            _txtFilterNicks.TextChanged += (_, _) => PopulateNicknamesList();
            searchRow.Controls.Add(_txtFilterNicks, 1, 0);

            _lblNickCount = new Label { AutoSize = true, ForeColor = AccentCyan, Anchor = AnchorStyles.Right, Padding = new Padding(8, 4, 0, 4) };
            searchRow.Controls.Add(_lblNickCount, 2, 0);
            searchCard.Controls.Add(searchRow);
            page.Controls.Add(searchCard, 0, 0);

            // 2. Nicknames List Card
            var listCard = CreateCard(new Padding(10));
            listCard.Dock = DockStyle.Fill;
            _lvNicknames = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                MultiSelect = false,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(14, 17, 24),
                ForeColor = TextPrimary,
                Font = new Font("Segoe UI", 9.25f)
            };
            _lvNicknames.Columns.Add("Type", 125);
            _lvNicknames.Columns.Add("When I Say (Nickname)", 215);
            _lvNicknames.Columns.Add("Maps To (HCS Place / Command)", 315);
            _lvNicknames.SelectedIndexChanged += (_, _) =>
            {
                if (_lvNicknames.SelectedItems.Count == 0) return;
                var item = _lvNicknames.SelectedItems[0];
                _cbNickKind.SelectedIndex = item.Text == "Command Alias" ? 1 : 0;
                _txtNickSpoken.Text = item.SubItems[1].Text;
                _cbNickTarget.Text = item.SubItems[2].Text;
            };
            listCard.Controls.Add(_lvNicknames);
            page.Controls.Add(listCard, 0, 1);

            // 3. Add / Edit / Import / Export Editor Card
            var editCard = CreateCard(new Padding(12, 10, 12, 10));
            editCard.AutoSize = true;
            var editGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
            editGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
            editGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
            editGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            editGrid.Controls.Add(CreateMutedLabel("Type"), 0, 0);
            editGrid.Controls.Add(CreateMutedLabel("Nickname (What You Say)"), 1, 0);
            editGrid.Controls.Add(CreateMutedLabel("Target HCS Destination or Command"), 2, 0);

            _cbNickKind = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgDark, ForeColor = TextPrimary };
            _cbNickKind.Items.AddRange(new object[] { "Place Nickname", "Command Alias" });
            _cbNickKind.SelectedIndex = 0;

            _txtNickSpoken = new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = BgDark,
                ForeColor = TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                PlaceholderText = "e.g. ash, babbage, home..."
            };

            _cbNickTarget = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems,
                BackColor = BgDark,
                ForeColor = TextPrimary
            };

            editGrid.Controls.Add(_cbNickKind, 0, 1);
            editGrid.Controls.Add(_txtNickSpoken, 1, 1);
            editGrid.Controls.Add(_cbNickTarget, 2, 1);

            var btnRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0, 8, 0, 0)
            };

            var btnAddSave = CreateFlatButton("Add / Update Nickname", 175);
            btnAddSave.BackColor = Color.FromArgb(14, 116, 144);
            btnAddSave.Click += (_, _) => SaveCurrentNicknameEntry();

            var btnDelete = CreateFlatButton("Delete Selected", 130);
            btnDelete.Margin = new Padding(8, 3, 3, 3);
            btnDelete.Click += (_, _) => DeleteSelectedNicknameEntry();

            var btnImport = CreateFlatButton("Import JSON...", 120);
            btnImport.Margin = new Padding(22, 3, 3, 3);
            btnImport.Click += (_, _) => ImportNicknamesJson();

            var btnExport = CreateFlatButton("Export JSON...", 120);
            btnExport.Margin = new Padding(8, 3, 3, 3);
            btnExport.Click += (_, _) => ExportNicknamesJson();

            btnRow.Controls.AddRange(new Control[] { btnAddSave, btnDelete, btnImport, btnExport });
            editGrid.Controls.Add(btnRow, 0, 2);
            editGrid.SetColumnSpan(btnRow, 3);

            editCard.Controls.Add(editGrid);
            page.Controls.Add(editCard, 0, 2);

            PopulateNicknamesList();
            return page;
        }

        private void SwitchPage(bool showGeneral)
        {
            _pageGeneral.Visible = showGeneral;
            _pageNicknames.Visible = !showGeneral;

            _tabBtnGeneral.BackColor = showGeneral ? Color.FromArgb(14, 116, 144) : Color.FromArgb(38, 47, 66);
            _tabBtnNicknames.BackColor = !showGeneral ? Color.FromArgb(14, 116, 144) : Color.FromArgb(38, 47, 66);

            if (!showGeneral)
            {
                RefreshDestinationsAutocomplete();
                PopulateNicknamesList();
            }
        }

        public void RefreshDestinationsAutocomplete()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(RefreshDestinationsAutocomplete)); return; }

            var places = _getKnownDestinations?.Invoke();
            if (places == null || places.Count == 0) return;

            string prevText = _cbNickTarget.Text;
            _cbNickTarget.BeginUpdate();
            _cbNickTarget.Items.Clear();
            foreach (string p in places)
                _cbNickTarget.Items.Add(p);
            _cbNickTarget.EndUpdate();
            _cbNickTarget.Text = prevText;
        }

        private void PopulateNicknamesList()
        {
            if (_lvNicknames == null || _nicknames == null) return;
            string filter = (_txtFilterNicks?.Text ?? "").Trim();

            _lvNicknames.BeginUpdate();
            _lvNicknames.Items.Clear();

            int count = 0;
            foreach (var kvp in _nicknames.PlaceNicknames.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (filter.Length > 0 &&
                    !kvp.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !kvp.Value.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var item = new ListViewItem("Place Nickname");
                item.SubItems.Add(kvp.Key);
                item.SubItems.Add(kvp.Value);
                _lvNicknames.Items.Add(item);
                count++;
            }

            foreach (var kvp in _nicknames.CommandAliases.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (filter.Length > 0 &&
                    !kvp.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !kvp.Value.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var item = new ListViewItem("Command Alias");
                item.SubItems.Add(kvp.Key);
                item.SubItems.Add(kvp.Value);
                _lvNicknames.Items.Add(item);
                count++;
            }

            _lvNicknames.EndUpdate();
            if (_lblNickCount != null)
                _lblNickCount.Text = $"{count} active";
        }

        private void SaveCurrentNicknameEntry()
        {
            string spoken = (_txtNickSpoken.Text ?? "").Trim();
            string target = (_cbNickTarget.Text ?? "").Trim();
            if (string.IsNullOrEmpty(spoken) || string.IsNullOrEmpty(target))
                return;

            if (_cbNickKind.SelectedIndex == 1)
                _nicknames.SetCommandAlias(spoken, target);
            else
                _nicknames.SetPlaceNickname(spoken, target);

            _nicknames.Save();
            _txtNickSpoken.Clear();
            PopulateNicknamesList();
            _onReloadTriggers?.Invoke();
            RefreshStatusLabel();
        }

        private void DeleteSelectedNicknameEntry()
        {
            if (_lvNicknames.SelectedItems.Count == 0) return;
            var sel = _lvNicknames.SelectedItems[0];
            string kind = sel.Text;
            string spoken = sel.SubItems[1].Text;

            if (kind == "Command Alias")
                _nicknames.RemoveCommandAlias(spoken);
            else
                _nicknames.RemovePlaceNickname(spoken);

            _nicknames.Save();
            PopulateNicknamesList();
            _onReloadTriggers?.Invoke();
            RefreshStatusLabel();
        }

        private void ImportNicknamesJson()
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Import HCSWhisper Nicknames JSON",
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*"
            };
            if (ofd.ShowDialog(this) != DialogResult.OK)
                return;

            var imported = NicknameStore.Load(ofd.FileName);
            foreach (var kvp in imported.PlaceNicknames)
                _nicknames.SetPlaceNickname(kvp.Key, kvp.Value);
            foreach (var kvp in imported.CommandAliases)
                _nicknames.SetCommandAlias(kvp.Key, kvp.Value);

            _nicknames.Save();
            PopulateNicknamesList();
            _onReloadTriggers?.Invoke();
            RefreshStatusLabel();
        }

        private void ExportNicknamesJson()
        {
            using var sfd = new SaveFileDialog
            {
                Title = "Export HCSWhisper Nicknames JSON",
                FileName = "hcswhisper-nicknames.json",
                Filter = "JSON Files (*.json)|*.json"
            };
            if (sfd.ShowDialog(this) == DialogResult.OK)
            {
                _nicknames.Save(sfd.FileName);
            }
        }

        public void RefreshStatusLabel()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(RefreshStatusLabel)); return; }
            _lblStatus.Text = _getStatusSummary?.Invoke() ?? "";
        }

        public void AppendLogLine(string message, Color color)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => AppendLogLine(message, color))); return; }

            _rtbLog.SelectionStart = _rtbLog.TextLength;
            _rtbLog.SelectionLength = 0;
            _rtbLog.SelectionColor = Color.FromArgb(100, 116, 139);
            _rtbLog.AppendText($"[{DateTime.Now:HH:mm:ss}] ");
            _rtbLog.SelectionColor = color;
            _rtbLog.AppendText(message + Environment.NewLine);
            _rtbLog.ScrollToCaret();
        }

        private async Task BindInputInteractiveAsync(bool isPtt)
        {
            _bindCts?.Cancel();
            _bindCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var targetBtn = isPtt ? _btnBindPtt : _btnBindSuppress;
            targetBtn.Text = "Press any Key, Mouse 4/5, or HOTAS Button... (Esc to cancel)";
            targetBtn.ForeColor = AccentCyan;

            string captured = await InputPoller.CaptureNextBindingAsync(_bindCts.Token);
            if (!string.IsNullOrEmpty(captured))
            {
                if (isPtt) _config.PttBinding = captured;
                else _config.SuppressBinding = captured;
                _config.Save();
            }

            targetBtn.ForeColor = TextPrimary;
            if (isPtt)
                _btnBindPtt.Text = $"PTT: {InputPoller.FormatFriendly(_config.PttBinding)}  (Click to Bind)";
            else
                _btnBindSuppress.Text = $"Mute Key: {InputPoller.FormatFriendly(_config.SuppressBinding)}  (Click to Bind)";
        }

        private void UpdateModeVisibility()
        {
            bool openMic = string.Equals(_config.Mode, "OpenMic", StringComparison.OrdinalIgnoreCase);
            _btnBindPtt.Enabled = !openMic;
            _tbVad.Enabled = openMic;
            _btnBindSuppress.Enabled = openMic;
            _btnClearSuppress.Enabled = openMic;
        }

        private static Panel CreateCard(Padding pad) => new()
        {
            BackColor = CardBg,
            Padding = pad,
            Margin = new Padding(0, 0, 0, 10),
            Dock = DockStyle.Top
        };

        private static Label CreateMutedLabel(string text) => new()
        {
            Text = text,
            UseMnemonic = false,
            ForeColor = TextMuted,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(0, 4, 0, 4)
        };

        private static Button CreateFlatButton(string text, int width)
        {
            var btn = new Button
            {
                Text = text,
                Width = width,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(38, 47, 66),
                ForeColor = TextPrimary,
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(65, 80, 110);
            return btn;
        }
    }

    internal static class GraphicsExtensions
    {
        public static void FillRoundedRectangleFallback(this Graphics g, Brush brush, int x, int y, int w, int h, int r)
        {
            using var path = new GraphicsPath();
            int d = r * 2;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            g.FillPath(brush, path);
        }
    }
}
