using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HCSWhisper
{
    public static class HcsWhisperRuntime
    {
        private static dynamic _proxy;
        private static string _pluginDir;
        private static HcsConfig _config;
        private static HcsTriggerIndex _triggerIndex;
        private static WhisperEngine _whisper;
        private static AudioCaptureEngine _audio;

        private static Thread _uiThread;
        private static SettingsForm _form;
        private static NotifyIcon _trayIcon;
        private static Icon _idleIcon;
        private static Icon _recIcon;

        private static Guid? _lastProfileId;
        private static CancellationTokenSource _shutdownCts;

        public static void StopCommand()
        {
            // Optional hook if VA stops all active commands
        }

        public static void Initialize(dynamic vaProxy, string pluginDir, string engineDir)
        {
            _proxy = vaProxy;
            _pluginDir = pluginDir;
            _shutdownCts = new CancellationTokenSource();

            _config = HcsConfig.Load();
            _triggerIndex = new HcsTriggerIndex();
            _whisper = new WhisperEngine(engineDir, LogToVaAndUi);
            _audio = new AudioCaptureEngine(_config, IsCrewAudioPlaying, LogToVaAndUi);

            _audio.RecordingStateChanged += OnRecordingStateChanged;
            _audio.UtteranceCompleted += OnUtteranceCompleted;

            StartTrayAndSettingsThread();

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_config.AutoMuteVaSpeech)
                    {
                        try
                        {
                            _proxy.State.SetListeningEnabled(false);
                            LogToVaAndUi("HCSWhisper: Disabled built-in Windows Speech listener (HCSWhisper active).", "blue");
                        }
                        catch { }
                    }

                    // Give HCS startup script ~1.5s to populate live hcsvt_* variables if VA just launched
                    await Task.Delay(1500, _shutdownCts.Token).ConfigureAwait(false);
                    ReloadHcsIndex();

                    await _whisper.LoadOrSwitchModelAsync(_config.ModelName, _triggerIndex.WhisperPrompt, _shutdownCts.Token).ConfigureAwait(false);
                    _form?.RefreshStatusLabel();

                    _audio.Start();
                    string modeDesc = string.Equals(_config.Mode, "OpenMic", StringComparison.OrdinalIgnoreCase)
                        ? "Open-Mic VAD"
                        : $"Push-to-Talk [{InputPoller.FormatFriendly(_config.PttBinding)}]";
                    LogToVaAndUi($"HCSWhisper active — Mode: {modeDesc} | Double-click cyan mic tray icon to configure.", "green");

                    await MonitorProfileChangesAsync(_shutdownCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    LogToVaAndUi($"HCSWhisper startup error: {ex.Message}", "red");
                }
            });
        }

        public static void Invoke(dynamic vaProxy)
        {
            string ctx = ((string)vaProxy.Context ?? "").Trim();
            if (ctx.Equals("Open Settings", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(ctx))
            {
                ShowSettingsWindow();
            }
            else if (ctx.Equals("Start Recording", StringComparison.OrdinalIgnoreCase) ||
                     ctx.Equals("Start Whisper Recording", StringComparison.OrdinalIgnoreCase))
            {
                _audio?.TriggerManualStart();
            }
            else if (ctx.Equals("Stop Recording", StringComparison.OrdinalIgnoreCase) ||
                     ctx.Equals("Stop Whisper Recording", StringComparison.OrdinalIgnoreCase))
            {
                _audio?.TriggerManualStop();
            }
            else if (ctx.Equals("Reload Triggers", StringComparison.OrdinalIgnoreCase))
            {
                ReloadHcsIndex();
            }
            else if (ctx.Equals("Toggle Pause", StringComparison.OrdinalIgnoreCase))
            {
                _config.Paused = !_config.Paused;
                _config.Save();
                LogToVaAndUi(_config.Paused ? "HCSWhisper paused" : "HCSWhisper resumed", "orange");
            }
        }

        public static void Shutdown()
        {
            try { _shutdownCts?.Cancel(); } catch { }
            try { _audio?.Dispose(); } catch { }
            try { _whisper?.Dispose(); } catch { }

            try
            {
                if (_trayIcon != null)
                    _trayIcon.Visible = false;
                if (_form != null && !_form.IsDisposed)
                    _form.BeginInvoke(new Action(() => Application.ExitThread()));
            }
            catch { }
        }

        private static void ReloadHcsIndex()
        {
            try
            {
                string appsDir = null;
                try { appsDir = _proxy.AppsDir; } catch { }
                if (string.IsNullOrEmpty(appsDir))
                    appsDir = Path.GetDirectoryName(_pluginDir);

                string profileName = "";
                try { profileName = _proxy.Profile.Name() ?? ""; } catch { }
                try { _lastProfileId = _proxy.Profile.InternalID(); } catch { }

                _triggerIndex.LoadFromVoiceAttack(
                    appsDir,
                    profileName,
                    varName =>
                    {
                        try { return _proxy.GetText(varName); }
                        catch { return null; }
                    },
                    phrase =>
                    {
                        try { return _proxy.Command.Exists(phrase); }
                        catch { return false; }
                    });

                LogToVaAndUi($"HCSWhisper indexed: {_triggerIndex.ActivePackSummary}", "blue");
                _form?.RefreshStatusLabel();
            }
            catch (Exception ex)
            {
                LogToVaAndUi($"HCSWhisper trigger index warning: {ex.Message}", "orange");
            }
        }

        private static async Task MonitorProfileChangesAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(3000, ct).ConfigureAwait(false);
                try
                {
                    Guid? currentId = _proxy.Profile.InternalID();
                    if (currentId != _lastProfileId)
                    {
                        _lastProfileId = currentId;
                        await Task.Delay(1200, ct).ConfigureAwait(false);
                        ReloadHcsIndex();
                    }
                }
                catch { }
            }
        }

        private static async void OnUtteranceCompleted(float[] samples)
        {
            try
            {
                var (rawText, whisperMs) = await _whisper.TranscribeAsync(samples, _triggerIndex.WhisperPrompt, _shutdownCts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(rawText))
                    return;

                var match = _triggerIndex.Match(
                    rawText,
                    phrase =>
                    {
                        try { return _proxy.Command.Exists(phrase); }
                        catch { return false; }
                    },
                    _config.FuzzyThreshold);

                if (match != null)
                {
                    string matchTag = match.IsExact ? "exact" : $"fuzzy {match.Score * 100:F0}%";
                    LogToVaAndUi($"Heard \"{match.RawText}\" → Executing \"{match.MatchedCommand}\" ({whisperMs} ms, {matchTag})", "green");
                    try
                    {
                        _proxy.Command.Execute(match.MatchedCommand, false, false, null, null, null, null, null, null);
                    }
                    catch (Exception ex)
                    {
                        LogToVaAndUi($"Failed executing '{match.MatchedCommand}': {ex.Message}", "red");
                    }
                }
                else
                {
                    LogToVaAndUi($"Heard \"{rawText}\" ({whisperMs} ms) — no matching HCS command above threshold", "orange");
                }
            }
            catch (Exception ex)
            {
                LogToVaAndUi($"HCSWhisper transcription error: {ex.Message}", "red");
            }
        }

        private static void OnRecordingStateChanged(bool recording)
        {
            try
            {
                if (_trayIcon != null)
                    _trayIcon.Icon = recording ? _recIcon : _idleIcon;
            }
            catch { }
        }

        private static bool IsCrewAudioPlaying()
        {
            try
            {
                return _proxy.State.AudioCount() > 0 || _proxy.State.SpeechActive();
            }
            catch
            {
                return false;
            }
        }

        private static string GetStatusSummary()
        {
            string backend = _whisper?.ActiveBackend ?? "Loading";
            string pack = _triggerIndex?.ActivePackSummary ?? "Indexing...";
            return $"GPU Backend: {backend}  |  {pack}";
        }

        private static void StartTrayAndSettingsThread()
        {
            _uiThread = new Thread(() =>
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                _idleIcon = SettingsForm.CreateDynamicAppIcon(recording: false);
                _recIcon = SettingsForm.CreateDynamicAppIcon(recording: true);

                _form = new SettingsForm(
                    _config,
                    _audio,
                    GetStatusSummary,
                    onModelChanged: () => _ = Task.Run(() => _whisper.LoadOrSwitchModelAsync(_config.ModelName, _triggerIndex.WhisperPrompt, _shutdownCts.Token)),
                    onReloadTriggers: ReloadHcsIndex);

                IntPtr unusedHandle = _form.Handle;

                var menu = new ContextMenuStrip();
                menu.Items.Add("Open HCSWhisper Settings...", null, (_, _) => ShowSettingsWindow());
                menu.Items.Add("Reload HCS Voice Triggers", null, (_, _) => ReloadHcsIndex());
                var pauseItem = new ToolStripMenuItem("Pause Listening") { Checked = _config.Paused, CheckOnClick = true };
                pauseItem.CheckedChanged += (_, _) =>
                {
                    _config.Paused = pauseItem.Checked;
                    _config.Save();
                };
                menu.Items.Add(pauseItem);

                _trayIcon = new NotifyIcon
                {
                    Icon = _idleIcon,
                    Text = "HCSWhisper — Double-click to configure",
                    ContextMenuStrip = menu,
                    Visible = true
                };
                _trayIcon.DoubleClick += (_, _) => ShowSettingsWindow();

                Application.Run();

                _trayIcon.Dispose();
                _idleIcon.Dispose();
                _recIcon.Dispose();
            })
            {
                IsBackground = true,
                Name = "HCSWhisper.UI"
            };
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.Start();
        }

        private static void ShowSettingsWindow()
        {
            if (_form == null || _form.IsDisposed) return;
            _form.BeginInvoke(new Action(() =>
            {
                _form.RefreshStatusLabel();
                _form.Show();
                _form.WindowState = FormWindowState.Normal;
                _form.Activate();
            }));
        }

        private static void LogToVaAndUi(string message, string vaColor)
        {
            try { _proxy?.WriteToLog(message, vaColor); } catch { }
            try
            {
                Color c = vaColor switch
                {
                    "green" => Color.FromArgb(52, 211, 153),
                    "red" => Color.FromArgb(248, 113, 113),
                    "orange" => Color.FromArgb(251, 191, 36),
                    "blue" => Color.FromArgb(56, 189, 248),
                    _ => Color.FromArgb(203, 213, 225)
                };
                _form?.AppendLogLine(message, c);
            }
            catch { }
        }
    }
}
