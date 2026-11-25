using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace HCSWhisper
{
    /// <summary>
    /// Zero-backend, zero-token GitHub Issue Reporter & Discord Clipboard Exporter.
    /// Pre-fills https://github.com/lordryn/HCSWhisper/issues/new with title, label,
    /// sanitized plugin diagnostics, and recent recognition log lines.
    /// </summary>
    public static class IssueReporter
    {
        private static readonly Regex HeardLineRegex = new(@"Heard\s+""([^""]+)""", RegexOptions.Compiled);

        public static string BuildMarkdownBody(
            string category,
            string spokenOrSummary,
            string expectedOrDetails,
            bool includeDiagnostics,
            string statusSummary,
            HcsConfig config,
            IReadOnlyList<string> recentLogLines)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"### Issue Type");
            sb.AppendLine(category);
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(spokenOrSummary))
            {
                sb.AppendLine("### What Was Spoken / Summary");
                sb.AppendLine($"`{spokenOrSummary.Trim()}`");
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(expectedOrDetails))
            {
                sb.AppendLine("### Expected Location / Command or Details");
                sb.AppendLine(expectedOrDetails.Trim());
                sb.AppendLine();
            }

            if (includeDiagnostics)
            {
                sb.AppendLine("### Environment & Diagnostics");
                sb.AppendLine($"- **HCSWhisper Version:** `v{HcsVersion.Current}`");
                sb.AppendLine($"- **Status:** `{SanitizeLine(statusSummary ?? "Unknown")}`");
                if (config != null)
                {
                    sb.AppendLine($"- **Whisper Model:** `{config.ModelName}`");
                    sb.AppendLine($"- **Listening Mode:** `{config.Mode}` (Fuzzy Threshold: `{config.FuzzyThreshold:P0}`)");
                }
                sb.AppendLine($"- **OS:** `{Environment.OSVersion.VersionString}` (64-bit)");
                sb.AppendLine();

                if (recentLogLines != null && recentLogLines.Count > 0)
                {
                    sb.AppendLine("### Recent Recognition Feed");
                    sb.AppendLine("```text");
                    foreach (string line in recentLogLines.TakeLast(12))
                    {
                        string clean = SanitizeLine(line);
                        if (!string.IsNullOrWhiteSpace(clean))
                            sb.AppendLine(clean);
                    }
                    sb.AppendLine("```");
                }
            }

            return sb.ToString().Trim();
        }

        public static string BuildGitHubNewIssueUrl(string category, string spokenOrSummary, string markdownBody)
        {
            string label = category switch
            {
                var c when c.Contains("Homophone", StringComparison.OrdinalIgnoreCase) => "enhancement",
                var c when c.Contains("Location", StringComparison.OrdinalIgnoreCase) => "enhancement",
                var c when c.Contains("Feature", StringComparison.OrdinalIgnoreCase) => "enhancement",
                _ => "bug"
            };

            string prefix = category switch
            {
                var c when c.Contains("Homophone", StringComparison.OrdinalIgnoreCase) => "[Voice Match] ",
                var c when c.Contains("Location", StringComparison.OrdinalIgnoreCase) => "[Location/Alias] ",
                var c when c.Contains("Feature", StringComparison.OrdinalIgnoreCase) => "[Feature] ",
                _ => "[Bug] "
            };

            string coreTitle = string.IsNullOrWhiteSpace(spokenOrSummary)
                ? category
                : spokenOrSummary.Trim();
            if (coreTitle.Length > 80)
                coreTitle = coreTitle.Substring(0, 80) + "...";

            string fullTitle = prefix + coreTitle;

            // Keep URL safely below browser/GitHub query-string limits (~6,500 chars encoded)
            string safeBody = markdownBody ?? "";
            if (safeBody.Length > 2800)
                safeBody = safeBody.Substring(0, 2800) + "\n...(truncated)";

            return $"https://github.com/{HcsVersion.GitHubOwner}/{HcsVersion.GitHubRepo}/issues/new" +
                   $"?title={Uri.EscapeDataString(fullTitle)}" +
                   $"&labels={Uri.EscapeDataString(label)}" +
                   $"&body={Uri.EscapeDataString(safeBody)}";
        }

        public static string ExtractMostRecentHeardPhrase(IReadOnlyList<string> recentLogLines)
        {
            if (recentLogLines == null || recentLogLines.Count == 0)
                return "";

            // Prefer the most recent missed ("no matching HCS command") phrase first, otherwise most recent Heard phrase
            for (int i = recentLogLines.Count - 1; i >= 0; i--)
            {
                string line = recentLogLines[i] ?? "";
                if (line.Contains("no matching HCS command", StringComparison.OrdinalIgnoreCase))
                {
                    var m = HeardLineRegex.Match(line);
                    if (m.Success)
                        return m.Groups[1].Value;
                }
            }

            for (int i = recentLogLines.Count - 1; i >= 0; i--)
            {
                var m = HeardLineRegex.Match(recentLogLines[i] ?? "");
                if (m.Success)
                    return m.Groups[1].Value;
            }

            return "";
        }

        private static string SanitizeLine(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            // Scrub Windows user profile paths just in case an exception message ever included a local path
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
                input = input.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            return input.Trim();
        }

        public static void ShowDialogOnUiThread(
            IWin32Window owner,
            HcsConfig config,
            string statusSummary,
            IReadOnlyList<string> recentLogLines)
        {
            using var dlg = new IssueReportDialog(config, statusSummary, recentLogLines);
            dlg.ShowDialog(owner);
        }
    }

    internal sealed class IssueReportDialog : Form
    {
        private readonly HcsConfig _config;
        private readonly string _statusSummary;
        private readonly IReadOnlyList<string> _recentLogLines;

        private readonly ComboBox _cbCategory;
        private readonly TextBox _txtSummary;
        private readonly TextBox _txtExpected;
        private readonly CheckBox _chkIncludeDiag;
        private readonly RichTextBox _rtbPreview;
        private readonly Button _btnCopyDiscord;

        private static readonly Color BgDark = Color.FromArgb(20, 24, 33);
        private static readonly Color CardBg = Color.FromArgb(29, 35, 48);
        private static readonly Color AccentCyan = Color.FromArgb(56, 189, 248);
        private static readonly Color TextPrimary = Color.FromArgb(235, 240, 248);
        private static readonly Color TextMuted = Color.FromArgb(148, 163, 184);

        public IssueReportDialog(HcsConfig config, string statusSummary, IReadOnlyList<string> recentLogLines)
        {
            _config = config;
            _statusSummary = statusSummary;
            _recentLogLines = recentLogLines ?? Array.Empty<string>();

            Text = "Report an Issue or Suggest a Homophone / Alias";
            Size = new Size(620, 560);
            MinimumSize = new Size(560, 500);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = BgDark;
            ForeColor = TextPrimary;
            Font = new Font("Segoe UI", 9.5f);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                ColumnCount = 1,
                RowCount = 6
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var lblIntro = new Label
            {
                AutoSize = true,
                ForeColor = AccentCyan,
                Font = new Font("Segoe UI Semibold", 10f),
                Margin = new Padding(0, 0, 0, 10),
                Text = "Submit a 1-click pre-filled GitHub Issue (or copy a report for Discord)"
            };
            root.Controls.Add(lblIntro, 0, 0);

            // Category + Spoken row
            var formCard = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                BackColor = CardBg,
                Padding = new Padding(12),
                Margin = new Padding(0, 0, 0, 10)
            };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            _cbCategory = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = BgDark,
                ForeColor = TextPrimary
            };
            _cbCategory.Items.AddRange(new object[]
            {
                "Misheard Place or Command (Homophone)",
                "Missing Location or Nickname",
                "Bug / Unexpected Behavior",
                "Feature Request"
            });
            _cbCategory.SelectedIndex = 0;
            _cbCategory.SelectedIndexChanged += (_, _) => RefreshPreview();

            string autoHeard = IssueReporter.ExtractMostRecentHeardPhrase(_recentLogLines);
            _txtSummary = new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = BgDark,
                ForeColor = TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Text = autoHeard,
                PlaceholderText = "e.g. Whisper heard \"Charles Beach\" or brief bug summary..."
            };
            _txtSummary.TextChanged += (_, _) => RefreshPreview();

            _txtExpected = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                Height = 54,
                BackColor = BgDark,
                ForeColor = TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                PlaceholderText = "e.g. Expected \"Plot a course to Chawla's Beach\" (or steps to reproduce)..."
            };
            _txtExpected.TextChanged += (_, _) => RefreshPreview();

            grid.Controls.Add(MakeLabel("Issue Category:"), 0, 0);
            grid.Controls.Add(_cbCategory, 1, 0);
            grid.Controls.Add(MakeLabel("Heard / Summary:"), 0, 1);
            grid.Controls.Add(_txtSummary, 1, 1);
            grid.Controls.Add(MakeLabel("Expected / Details:"), 0, 2);
            grid.Controls.Add(_txtExpected, 1, 2);
            formCard.Controls.Add(grid);
            root.Controls.Add(formCard, 0, 1);

            _chkIncludeDiag = new CheckBox
            {
                Text = "Include recent recognition feed & plugin diagnostics (no personal paths)",
                Checked = true,
                AutoSize = true,
                ForeColor = TextMuted,
                Margin = new Padding(0, 0, 0, 6)
            };
            _chkIncludeDiag.CheckedChanged += (_, _) => RefreshPreview();
            root.Controls.Add(_chkIncludeDiag, 0, 2);

            root.Controls.Add(MakeLabel("Report Preview (editable before sending):"), 0, 3);

            _rtbPreview = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(14, 17, 24),
                ForeColor = TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9f),
                Margin = new Padding(0, 2, 0, 12)
            };
            root.Controls.Add(_rtbPreview, 0, 4);

            var btnRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                WrapContents = false
            };

            var btnOpenGitHub = MakeButton("Open Pre-Filled GitHub Issue ↗", 225, Color.FromArgb(14, 116, 144));
            btnOpenGitHub.Font = new Font("Segoe UI Semibold", 9.5f);
            btnOpenGitHub.Click += (_, _) =>
            {
                string url = IssueReporter.BuildGitHubNewIssueUrl(
                    _cbCategory.SelectedItem?.ToString() ?? "Issue",
                    _txtSummary.Text,
                    _rtbPreview.Text);
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                    DialogResult = DialogResult.OK;
                    Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Could not launch browser: {ex.Message}", "HCSWhisper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            _btnCopyDiscord = MakeButton("Copy Report (Discord)", 170, Color.FromArgb(38, 47, 66));
            _btnCopyDiscord.Margin = new Padding(0, 3, 8, 3);
            _btnCopyDiscord.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(_rtbPreview.Text);
                    _btnCopyDiscord.Text = "Copied to Clipboard! ✓";
                    _btnCopyDiscord.ForeColor = Color.FromArgb(52, 211, 153);
                }
                catch { }
            };

            var btnCancel = MakeButton("Cancel", 90, Color.FromArgb(38, 47, 66));
            btnCancel.Margin = new Padding(0, 3, 8, 3);
            btnCancel.Click += (_, _) => Close();

            btnRow.Controls.Add(btnOpenGitHub);
            btnRow.Controls.Add(_btnCopyDiscord);
            btnRow.Controls.Add(btnCancel);
            root.Controls.Add(btnRow, 0, 5);

            Controls.Add(root);
            RefreshPreview();
        }

        private void RefreshPreview()
        {
            if (_rtbPreview == null) return;
            _rtbPreview.Text = IssueReporter.BuildMarkdownBody(
                _cbCategory.SelectedItem?.ToString() ?? "Bug",
                _txtSummary.Text,
                _txtExpected.Text,
                _chkIncludeDiag.Checked,
                _statusSummary,
                _config,
                _recentLogLines);
        }

        private static Label MakeLabel(string text) => new()
        {
            Text = text,
            UseMnemonic = false,
            ForeColor = TextMuted,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(0, 5, 0, 5)
        };

        private static Button MakeButton(string text, int width, Color bg)
        {
            var b = new Button
            {
                Text = text,
                Width = width,
                Height = 32,
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = TextPrimary,
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(65, 80, 110);
            return b;
        }
    }
}
