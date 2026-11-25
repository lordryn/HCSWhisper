using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HCSWhisper
{
    public sealed class ReleaseUpdateInfo
    {
        public string LatestVersion { get; init; }
        public string TagName { get; init; }
        public string ReleaseNotes { get; init; }
        public string ZipDownloadUrl { get; init; }
        public string HtmlUrl { get; init; }
    }

    /// <summary>
    /// Checks GitHub Releases for newer HCSWhisper builds and provides a 1-click
    /// "Update & Restart VoiceAttack" prompt that safely swaps locked plugin files via a helper script.
    /// </summary>
    public static class AutoUpdater
    {
        private static readonly HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"HCSWhisper/{HcsVersion.Current}");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        public static async Task<ReleaseUpdateInfo> CheckLatestReleaseAsync(CancellationToken ct = default)
        {
            string url = $"https://api.github.com/repos/{HcsVersion.GitHubOwner}/{HcsVersion.GitHubRepo}/releases/latest";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var tProp) ? (tProp.GetString() ?? "") : "";
            string cleanVer = tag.TrimStart('v', 'V').Trim();
            if (!Version.TryParse(cleanVer, out Version latest) ||
                !Version.TryParse(HcsVersion.Current, out Version current) ||
                latest <= current)
            {
                return null;
            }

            string body = root.TryGetProperty("body", out var bProp) ? (bProp.GetString() ?? "") : "";
            string htmlUrl = root.TryGetProperty("html_url", out var hProp) ? (hProp.GetString() ?? "") : "";
            string zipUrl = null;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? "") : "";
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zipUrl = asset.TryGetProperty("browser_download_url", out var dProp) ? dProp.GetString() : null;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(zipUrl))
                return null;

            return new ReleaseUpdateInfo
            {
                LatestVersion = cleanVer,
                TagName = tag,
                ReleaseNotes = body,
                ZipDownloadUrl = zipUrl,
                HtmlUrl = htmlUrl
            };
        }

        public static void ShowUpdatePromptOnUiThread(
            Form owner,
            ReleaseUpdateInfo info,
            HcsConfig config,
            string pluginDir,
            Action requestVoiceAttackClose,
            Action<string, string> log)
        {
            if (owner == null || owner.IsDisposed)
                return;

            if (owner.InvokeRequired)
            {
                owner.BeginInvoke(new Action(() =>
                    ShowUpdatePromptOnUiThread(owner, info, config, pluginDir, requestVoiceAttackClose, log)));
                return;
            }

            using var dlg = new UpdatePromptDialog(info, config, pluginDir, requestVoiceAttackClose, log);
            dlg.ShowDialog(owner.Visible ? owner : null);
        }

        internal static async Task DownloadAndLaunchUpdaterAsync(
            ReleaseUpdateInfo info,
            string pluginDir,
            Action<string> onProgress,
            Action requestVoiceAttackClose)
        {
            string tempZip = Path.Combine(Path.GetTempPath(), $"HCSWhisper-update-{info.LatestVersion}.zip");
            onProgress?.Invoke($"Downloading HCSWhisper v{info.LatestVersion}...");

            using (var resp = await Http.GetAsync(info.ZipDownloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long? totalBytes = resp.Content.Headers.ContentLength;
                using var netStream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None);

                byte[] buf = new byte[65536];
                long readTotal = 0;
                int read;
                while ((read = await netStream.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buf, 0, read).ConfigureAwait(false);
                    readTotal += read;
                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        int pct = (int)(readTotal * 100L / totalBytes.Value);
                        onProgress?.Invoke($"Downloading HCSWhisper v{info.LatestVersion}... {pct}%");
                    }
                }
            }

            onProgress?.Invoke("Applying update and restarting VoiceAttack 2...");

            string appsDir = Path.GetDirectoryName(pluginDir) ?? Path.Combine(AppContext.BaseDirectory, "Apps");
            string vaRoot = Path.GetDirectoryName(appsDir) ?? AppContext.BaseDirectory;
            string vaExe = Path.Combine(vaRoot, "VoiceAttack.exe");
            bool isSteam = File.Exists(Path.Combine(vaRoot, "steam_api64.dll")) ||
                           vaRoot.Contains("steamapps", StringComparison.OrdinalIgnoreCase);

            int currentPid = Process.GetCurrentProcess().Id;
            string engineStamp = Path.Combine(HcsConfig.EngineDir, ".bundle-version");
            string updaterScript = Path.Combine(Path.GetTempPath(), "hcswhisper_self_update.ps1");

            string psContent = $@"
$ErrorActionPreference = 'SilentlyContinue'
for ($i = 0; $i -lt 40; $i++) {{
    if (-not (Get-Process -Id {currentPid} -ErrorAction SilentlyContinue)) {{ break }}
    Start-Sleep -Milliseconds 250
}}
Start-Sleep -Milliseconds 400
$appsDir = '{appsDir.Replace("'", "''")}'
$targetDir = Join-Path $appsDir 'HCSWhisper'
if (Test-Path $targetDir) {{ Remove-Item -Recurse -Force $targetDir }}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory('{tempZip.Replace("'", "''")}', $appsDir)
Remove-Item -Force '{tempZip.Replace("'", "''")}' -ErrorAction SilentlyContinue
Remove-Item -Force '{engineStamp.Replace("'", "''")}' -ErrorAction SilentlyContinue
{(isSteam ? "explorer.exe 'steam://rungameid/3046550'" : $"Start-Process -FilePath '{vaExe.Replace("'", "''")}' -WorkingDirectory '{vaRoot.Replace("'", "''")}'")}
";
            File.WriteAllText(updaterScript, psContent);

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{updaterScript}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            requestVoiceAttackClose?.Invoke();
        }
    }

    internal sealed class UpdatePromptDialog : Form
    {
        private readonly ReleaseUpdateInfo _info;
        private readonly HcsConfig _config;
        private readonly string _pluginDir;
        private readonly Action _requestVaClose;
        private readonly Action<string, string> _log;

        private readonly Label _lblStatus;
        private readonly Button _btnUpdate;
        private readonly Button _btnLater;
        private readonly Button _btnSkip;

        public UpdatePromptDialog(
            ReleaseUpdateInfo info,
            HcsConfig config,
            string pluginDir,
            Action requestVaClose,
            Action<string, string> log)
        {
            _info = info;
            _config = config;
            _pluginDir = pluginDir;
            _requestVaClose = requestVaClose;
            _log = log;

            Text = $"HCSWhisper Update Available — v{info.LatestVersion}";
            Size = new Size(520, 360);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            BackColor = Color.FromArgb(20, 24, 33);
            ForeColor = Color.FromArgb(235, 240, 248);
            Font = new Font("Segoe UI", 9.5f);
            Icon = SettingsForm.CreateDynamicAppIcon(false);

            var lblTitle = new Label
            {
                Text = $"A new version of HCSWhisper is available! (v{HcsVersion.Current} → v{info.LatestVersion})",
                ForeColor = Color.FromArgb(56, 189, 248),
                Font = new Font("Segoe UI Semibold", 10.5f),
                AutoSize = false,
                Location = new Point(18, 16),
                Size = new Size(470, 26)
            };

            var txtNotes = new RichTextBox
            {
                Location = new Point(18, 48),
                Size = new Size(468, 195),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(29, 35, 48),
                ForeColor = Color.FromArgb(220, 228, 240),
                Font = new Font("Segoe UI", 9f),
                Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
                    ? $"HCSWhisper v{info.LatestVersion} is ready to install."
                    : info.ReleaseNotes
            };

            _lblStatus = new Label
            {
                Location = new Point(18, 252),
                Size = new Size(468, 22),
                ForeColor = Color.FromArgb(148, 163, 184),
                Text = "Click 'Update & Restart' to install automatically in ~5 seconds."
            };

            _btnUpdate = CreateBtn("Update && Restart VoiceAttack", new Point(18, 282), 220, Color.FromArgb(14, 116, 144));
            _btnLater = CreateBtn("Remind Me Later", new Point(248, 282), 125, Color.FromArgb(38, 47, 66));
            _btnSkip = CreateBtn("Skip Version", new Point(381, 282), 105, Color.FromArgb(38, 47, 66));

            _btnUpdate.Click += async (_, _) => await RunUpdateAsync();
            _btnLater.Click += (_, _) => Close();
            _btnSkip.Click += (_, _) =>
            {
                _config.SkippedUpdateVersion = _info.LatestVersion;
                _config.Save();
                Close();
            };

            Controls.AddRange(new Control[] { lblTitle, txtNotes, _lblStatus, _btnUpdate, _btnLater, _btnSkip });
        }

        private async Task RunUpdateAsync()
        {
            _btnUpdate.Enabled = false;
            _btnLater.Enabled = false;
            _btnSkip.Enabled = false;

            try
            {
                await AutoUpdater.DownloadAndLaunchUpdaterAsync(
                    _info,
                    _pluginDir,
                    msg =>
                    {
                        if (!IsDisposed)
                            BeginInvoke(new Action(() => _lblStatus.Text = msg));
                    },
                    _requestVaClose);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"HCSWhisper auto-update error: {ex.Message}", "red");
                if (!IsDisposed)
                {
                    _lblStatus.ForeColor = Color.FromArgb(248, 113, 113);
                    _lblStatus.Text = "Update failed: " + ex.Message;
                    _btnUpdate.Enabled = true;
                    _btnLater.Enabled = true;
                }
            }
        }

        private static Button CreateBtn(string text, Point loc, int width, Color bg)
        {
            var b = new Button
            {
                Text = text,
                Location = loc,
                Size = new Size(width, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
            return b;
        }
    }
}
