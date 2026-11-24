using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HCSWhisper
{
    /// <summary>
    /// Dark-themed HCSWhisper Control Panel & System Tray Icon.
    /// Allows 1-click HOTAS/Mouse/Keyboard Push-to-Talk binding, Open-Mic VAD tuning,
    /// model selection, and live millisecond transcription monitoring.
    /// </summary>
    public sealed class SettingsForm : Form
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        private readonly HcsConfig _config;
        private readonly AudioCaptureEngine _audio;
        private readonly Func<string> _getStatusSummary;
        private readonly Action _onModelChanged;
        private readonly Action _onReloadTriggers;

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
        private Label _lblStatus;
        private Panel _meterFill;
        private Panel _meterBg;
        private RichTextBox _rtbLog;
        private System.Windows.Forms.Timer _uiTimer;
        private CancellationTokenSource _bindCts;

        private static readonly Color BgDark = Color.FromArgb(20, 24, 33);
        private static readonly Color CardBg = Color.FromArgb(29, 35, 48);
        private static readonly Color AccentCyan = Color.FromArgb(56, 189, 248);
        private static readonly Color AccentGreen = Color.FromArgb(52, 211, 153);
        private static readonly Color TextPrimary = Color.FromArgb(235, 240, 248);
        private static readonly Color TextMuted = Color.FromArgb(148, 163, 184);

        public SettingsForm(
            HcsConfig config,
            AudioCaptureEngine audio,
            Func<string> getStatusSummary,
            Action onModelChanged,
            Action onReloadTriggers)
        {
            _config = config;
            _audio = audio;
            _getStatusSummary = getStatusSummary;
            _onModelChanged = onModelChanged;
            _onReloadTriggers = onReloadTriggers;

            BuildUi();
        }

        public static Icon CreateDynamicAppIcon(bool recording = false)
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var bgBrush = new SolidBrush(recording ? Color.FromArgb(220, 38, 38) : Color.FromArgb(15, 23, 42));
                var ringPen = new Pen(recording ? Color.FromArgb(252, 165, 165) : Color.FromArgb(56, 189, 248), 2.2f);
                g.FillEllipse(bgBrush, 1, 1, 29, 29);
                g.DrawEllipse(ringPen, 2, 2, 27, 27);

                // Microphone capsule
                using var fg = new SolidBrush(Color.White);
                g.FillRoundedRectangleFallback(fg, 12, 7, 8, 12, 4);
                using var arcPen = new Pen(Color.White, 2f);
                g.DrawArc(arcPen, 9, 10, 14, 12, 0, 180);
                g.DrawLine(arcPen, 16, 22, 16, 26);
                g.DrawLine(arcPen, 12, 26, 20, 26);

                bgBrush.Dispose();
                ringPen.Dispose();
            }

            IntPtr hIcon = bmp.GetHicon();
            Icon cloned = (Icon)Icon.FromHandle(hIcon).Clone();
            DestroyIcon(hIcon);
            return cloned;
        }

        private void BuildUi()
        {
            Text = "HCSWhisper — AI Speech Engine for VoiceAttack & HCS Voice Packs";
            Size = new Size(680, 620);
            MinimumSize = new Size(640, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BgDark;
            ForeColor = TextPrimary;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            Icon = CreateDynamicAppIcon(false);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                ColumnCount = 1,
                RowCount = 4
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            // 1. Header Status Card
            var statusCard = CreateCard(new Padding(12, 10, 12, 10));
            _lblStatus = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ForeColor = AccentCyan,
                Font = new Font("Segoe UI Semibold", 9.5f),
                Text = _getStatusSummary?.Invoke() ?? "Initializing HCSWhisper..."
            };
            var btnReload = CreateFlatButton("Reload HCS Pack", 130);
            btnReload.Dock = DockStyle.Right;
            btnReload.Click += (_, _) =>
            {
                _onReloadTriggers?.Invoke();
                RefreshStatusLabel();
            };
            statusCard.Controls.Add(_lblStatus);
            statusCard.Controls.Add(btnReload);
            statusCard.Height = 46;
            root.Controls.Add(statusCard, 0, 0);

            // 2. Activation & Bindings Card
            var bindCard = CreateCard(new Padding(14, 12, 14, 12));
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

            var vadFlow = new Panel { Height = 32, Dock = DockStyle.Fill };
            _tbVad = new TrackBar { Minimum = 4, Maximum = 80, Value = Math.Clamp((int)(_config.VadRmsThreshold * 1000), 4, 80), TickStyle = TickStyle.None, Width = 260, Height = 28, Left = 0, Top = 2 };
            _lblVadVal = new Label { AutoSize = true, Left = 270, Top = 6, ForeColor = TextMuted, Text = $"{_tbVad.Value / 10.0:F1}% threshold" };
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
            root.Controls.Add(bindCard, 0, 1);

            // 3. Audio & AI Model Card
            var modelCard = CreateCard(new Padding(14, 12, 14, 12));
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
            _chkBeep = new CheckBox { Text = "PTT audio cue beep", Checked = _config.PlayPttBeep, AutoSize = true, ForeColor = TextMuted };
            _chkBeep.CheckedChanged += (_, _) => { _config.PlayPttBeep = _chkBeep.Checked; _config.Save(); };
            _chkAutoMuteVa = new CheckBox { Text = "Auto-disable Windows Speech in VoiceAttack", Checked = _config.AutoMuteVaSpeech, AutoSize = true, ForeColor = TextMuted, Margin = new Padding(18, 3, 0, 0) };
            _chkAutoMuteVa.CheckedChanged += (_, _) => { _config.AutoMuteVaSpeech = _chkAutoMuteVa.Checked; _config.Save(); };
            optsFlow.Controls.Add(_chkBeep);
            optsFlow.Controls.Add(_chkAutoMuteVa);
            modelLayout.Controls.Add(CreateMutedLabel("Options:"), 0, 3);
            modelLayout.Controls.Add(optsFlow, 1, 3);

            modelCard.Controls.Add(modelLayout);
            modelCard.AutoSize = true;
            root.Controls.Add(modelCard, 0, 2);

            // 4. Live Recognition Feed
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
            root.Controls.Add(logCard, 0, 3);

            Controls.Add(root);
            UpdateModeVisibility();

            _uiTimer = new System.Windows.Forms.Timer { Interval = 50 };
            _uiTimer.Tick += (_, _) =>
            {
                if (!Visible || _audio == null) return;
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
