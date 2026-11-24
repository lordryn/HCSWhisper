using System;
using System.Collections.Generic;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace HCSWhisper
{
    public sealed class MicDeviceInfo
    {
        public string Id { get; init; }
        public string Name { get; init; }
        public override string ToString() => Name;
    }

    /// <summary>
    /// Continuous warm WASAPI shared-mode audio capture with 250ms pre-roll buffer,
    /// automatic multi-channel/sample-rate conversion to 16 kHz mono float32,
    /// Push-to-Talk polling, and Open-Mic Voice Activity Detection (with crew-audio ducking).
    /// </summary>
    public sealed class AudioCaptureEngine : IDisposable
    {
        private static readonly Guid SubTypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

        private const int TargetSampleRate = 16000;
        private const int PreRollSamples = 4000; // 250ms pre-roll so initial syllables are never clipped
        private const int MaxUtteranceSamples = TargetSampleRate * 10; // 10s safety cap
        private const int MinUtteranceSamples = 3200; // 200ms minimum to ignore accidental taps

        private readonly HcsConfig _config;
        private readonly Func<bool> _isCrewAudioPlaying;
        private readonly Action<string, string> _log;

        private WasapiCapture _capture;
        private WaveFormat _sourceFormat;
        private double _resamplePos;

        private readonly object _bufferLock = new();
        private readonly float[] _preRollRing = new float[PreRollSamples];
        private int _preRollWritePos;
        private int _preRollCount;

        private readonly List<float> _activeRecording = new(TargetSampleRate * 4);
        private volatile bool _isCapturingUtterance;
        private volatile bool _manualOverrideActive;
        private volatile float _currentRms;

        private CancellationTokenSource _pollCts;
        private Task _pollTask;

        public event Action<float[]> UtteranceCompleted;
        public event Action<bool> RecordingStateChanged;

        public float CurrentRms => _currentRms;
        public bool IsRecording => _isCapturingUtterance;

        public AudioCaptureEngine(HcsConfig config, Func<bool> isCrewAudioPlaying, Action<string, string> log)
        {
            _config = config;
            _isCrewAudioPlaying = isCrewAudioPlaying;
            _log = log;
        }

        public static List<MicDeviceInfo> GetCaptureDevices()
        {
            var list = new List<MicDeviceInfo>
            {
                new MicDeviceInfo { Id = "", Name = "Windows Default Microphone" }
            };
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    list.Add(new MicDeviceInfo { Id = dev.ID, Name = dev.FriendlyName });
                }
            }
            catch
            {
                // Ignore audio enumeration issues on headless setups
            }
            return list;
        }

        public void Start()
        {
            Stop();

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice device = null;
                if (!string.IsNullOrWhiteSpace(_config.MicDeviceId))
                {
                    try { device = enumerator.GetDevice(_config.MicDeviceId); }
                    catch { device = null; }
                }
                device ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);

                _capture = new WasapiCapture(device, true, 25);
                _sourceFormat = _capture.WaveFormat;
                _resamplePos = 0;
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();

                _pollCts = new CancellationTokenSource();
                _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token));

                _log?.Invoke($"HCSWhisper mic ready: {device.FriendlyName} ({_sourceFormat.SampleRate} Hz, {_sourceFormat.Channels} ch)", "blue");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"HCSWhisper failed to open microphone: {ex.Message}", "red");
            }
        }

        public void RestartDevice() => Start();

        public void TriggerManualStart() => _manualOverrideActive = true;
        public void TriggerManualStop() => _manualOverrideActive = false;

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
                _log?.Invoke($"Microphone stream interrupted: {e.Exception.Message}", "orange");
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0 || _sourceFormat == null)
                return;

            int bytesPerFrame = _sourceFormat.BlockAlign;
            if (bytesPerFrame <= 0)
                return;

            int frameCount = e.BytesRecorded / bytesPerFrame;
            if (frameCount <= 0)
                return;

            int channels = _sourceFormat.Channels;
            int bits = _sourceFormat.BitsPerSample;
            bool isFloat = _sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (_sourceFormat is WaveFormatExtensible ext && ext.SubFormat == SubTypeIeeeFloat);

            // Convert incoming native frames to mono float [-1..1]
            float[] monoSrc = new float[frameCount];
            byte[] buf = e.Buffer;
            int offset = 0;

            for (int f = 0; f < frameCount; f++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                {
                    float sample = 0f;
                    if (isFloat && bits == 32)
                    {
                        sample = BitConverter.ToSingle(buf, offset);
                        offset += 4;
                    }
                    else if (bits == 16)
                    {
                        sample = BitConverter.ToInt16(buf, offset) / 32768f;
                        offset += 2;
                    }
                    else if (bits == 24)
                    {
                        int s24 = (buf[offset] | (buf[offset + 1] << 8) | ((sbyte)buf[offset + 2] << 16));
                        sample = s24 / 8388608f;
                        offset += 3;
                    }
                    else if (bits == 32)
                    {
                        sample = BitConverter.ToInt32(buf, offset) / 2147483648f;
                        offset += 4;
                    }
                    else
                    {
                        offset += Math.Max(1, bits / 8);
                    }
                    sum += sample;
                }
                monoSrc[f] = sum / channels;
            }

            // Resample monoSrc from _sourceFormat.SampleRate -> 16000 Hz
            double step = (double)_sourceFormat.SampleRate / TargetSampleRate;
            var resampled = new List<float>((int)(frameCount / step) + 2);
            double pos = _resamplePos;
            while (pos < frameCount)
            {
                int idx = (int)pos;
                double frac = pos - idx;
                float s0 = monoSrc[idx];
                float s1 = (idx + 1 < frameCount) ? monoSrc[idx + 1] : s0;
                resampled.Add((float)(s0 + (s1 - s0) * frac));
                pos += step;
            }
            _resamplePos = pos - frameCount;
            if (_resamplePos < 0) _resamplePos = 0;

            if (resampled.Count == 0)
                return;

            // Compute RMS for UI meter & VAD
            double sq = 0;
            for (int i = 0; i < resampled.Count; i++)
                sq += resampled[i] * resampled[i];
            float rms = (float)Math.Sqrt(sq / resampled.Count);
            _currentRms = _currentRms * 0.6f + rms * 0.4f;

            lock (_bufferLock)
            {
                if (_isCapturingUtterance)
                {
                    int room = MaxUtteranceSamples - _activeRecording.Count;
                    if (room > 0)
                    {
                        int countToAdd = Math.Min(room, resampled.Count);
                        for (int i = 0; i < countToAdd; i++)
                            _activeRecording.Add(resampled[i]);
                    }
                }
                else
                {
                    // Feed 250ms pre-roll ring buffer
                    for (int i = 0; i < resampled.Count; i++)
                    {
                        _preRollRing[_preRollWritePos] = resampled[i];
                        _preRollWritePos = (_preRollWritePos + 1) % PreRollSamples;
                        if (_preRollCount < PreRollSamples)
                            _preRollCount++;
                    }
                }
            }
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            bool wasPttDown = false;
            int vadAboveTicks = 0;
            long lastVoiceTickMs = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_config.Paused)
                    {
                        if (_isCapturingUtterance)
                            FinishUtterance(discard: true);
                        wasPttDown = false;
                        await Task.Delay(30, ct).ConfigureAwait(false);
                        continue;
                    }

                    bool isOpenMic = string.Equals(_config.Mode, "OpenMic", StringComparison.OrdinalIgnoreCase);

                    if (!isOpenMic)
                    {
                        // Push-to-Talk Mode
                        bool pttDown = _manualOverrideActive || InputPoller.IsBindingPressed(_config.PttBinding);
                        if (pttDown && !wasPttDown)
                        {
                            BeginUtterance(includePreRoll: true);
                            if (_config.PlayPttBeep)
                                _ = Task.Run(() => Console.Beep(880, 35));
                        }
                        else if (!pttDown && wasPttDown)
                        {
                            if (_config.PlayPttBeep)
                                _ = Task.Run(() => Console.Beep(660, 35));
                            FinishUtterance(discard: false);
                        }
                        wasPttDown = pttDown;
                    }
                    else
                    {
                        // Open-Mic VAD Mode (suppresses while Discord PTT is held or HCS crew is speaking)
                        bool suppressed = (!string.IsNullOrWhiteSpace(_config.SuppressBinding) && InputPoller.IsBindingPressed(_config.SuppressBinding)) ||
                                          (_isCrewAudioPlaying != null && _isCrewAudioPlaying());

                        if (suppressed)
                        {
                            vadAboveTicks = 0;
                            if (_isCapturingUtterance)
                                FinishUtterance(discard: false);
                        }
                        else
                        {
                            long nowMs = Environment.TickCount64;
                            bool voiceNow = _currentRms >= _config.VadRmsThreshold;

                            if (!_isCapturingUtterance)
                            {
                                if (voiceNow)
                                {
                                    vadAboveTicks++;
                                    if (vadAboveTicks >= 5) // ~60ms sustained speech starts utterance
                                    {
                                        lastVoiceTickMs = nowMs;
                                        BeginUtterance(includePreRoll: true);
                                    }
                                }
                                else
                                {
                                    vadAboveTicks = 0;
                                }
                            }
                            else
                            {
                                if (voiceNow)
                                {
                                    lastVoiceTickMs = nowMs;
                                }
                                else if (nowMs - lastVoiceTickMs >= _config.VadSilenceHangoverMs)
                                {
                                    vadAboveTicks = 0;
                                    FinishUtterance(discard: false);
                                }

                                // Safety cap on continuous noise in Open-Mic
                                lock (_bufferLock)
                                {
                                    if (_activeRecording.Count >= MaxUtteranceSamples)
                                    {
                                        vadAboveTicks = 0;
                                        FinishUtterance(discard: false);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Keep poll loop alive
                }

                await Task.Delay(12, ct).ConfigureAwait(false);
            }
        }

        private void BeginUtterance(bool includePreRoll)
        {
            lock (_bufferLock)
            {
                _activeRecording.Clear();
                if (includePreRoll && _preRollCount > 0)
                {
                    // Copy last ~160ms (2560 samples) of pre-roll so fast PTT presses never clip first phoneme
                    int take = Math.Min(_preRollCount, 2560);
                    int startIdx = (_preRollWritePos - take + PreRollSamples) % PreRollSamples;
                    for (int i = 0; i < take; i++)
                    {
                        _activeRecording.Add(_preRollRing[(startIdx + i) % PreRollSamples]);
                    }
                }
                _preRollCount = 0;
                _preRollWritePos = 0;
                _isCapturingUtterance = true;
            }
            RecordingStateChanged?.Invoke(true);
        }

        private void FinishUtterance(bool discard)
        {
            float[] captured = null;
            lock (_bufferLock)
            {
                if (!_isCapturingUtterance)
                    return;
                _isCapturingUtterance = false;
                if (!discard && _activeRecording.Count >= MinUtteranceSamples)
                {
                    captured = _activeRecording.ToArray();
                }
                _activeRecording.Clear();
            }

            RecordingStateChanged?.Invoke(false);

            if (captured != null)
            {
                var samples = captured;
                Task.Run(() => UtteranceCompleted?.Invoke(samples));
            }
        }

        public void Stop()
        {
            try { _pollCts?.Cancel(); } catch { }
            try { _capture?.StopRecording(); } catch { }
            try { _capture?.Dispose(); } catch { }
            _capture = null;
        }

        public void Dispose() => Stop();
    }
}
