using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace HCSWhisper
{
    /// <summary>
    /// GPU-accelerated (Vulkan -> CPU fallback) Whisper inference engine.
    /// Zero Python, works on both NVIDIA and AMD GPUs, dynamically scales AudioContextSize
    /// to utterance length for ~50-90ms command transcription.
    /// </summary>
    public sealed class WhisperEngine : IDisposable
    {
        private static bool _runtimeConfigured;
        private static readonly object _runtimeInitLock = new();

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly string _pluginDir;
        private readonly Action<string, string> _log;

        private WhisperFactory _factory;
        private string _loadedModelName;

        public string ActiveBackend => RuntimeOptions.LoadedLibrary?.ToString() ?? "Initializing";
        public bool IsReady => _factory != null;

        public WhisperEngine(string engineDir, Action<string, string> log)
        {
            _pluginDir = engineDir;
            _log = log;
            EnsureRuntimeConfigured(engineDir);
        }

        private static void EnsureRuntimeConfigured(string pluginDir)
        {
            lock (_runtimeInitLock)
            {
                if (_runtimeConfigured)
                    return;

                RuntimeOptions.LibraryPath = pluginDir;
                RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary>
                {
                    RuntimeLibrary.Vulkan,
                    RuntimeLibrary.Cpu,
                    RuntimeLibrary.CpuNoAvx
                };
                _runtimeConfigured = true;
            }
        }

        public async Task LoadOrSwitchModelAsync(string modelName, string initialPrompt, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var (ggmlType, fileName) = ResolveModelSpec(modelName);
                if (_factory != null && string.Equals(_loadedModelName, fileName, StringComparison.OrdinalIgnoreCase))
                    return;

                _factory?.Dispose();
                _factory = null;

                string modelPath = Path.Combine(HcsConfig.ModelsDir, fileName);
                if (!File.Exists(modelPath) || new FileInfo(modelPath).Length < 10_000_000)
                {
                    _log?.Invoke($"HCSWhisper: Downloading Whisper model '{modelName}' (one-time download)...", "blue");
                    string tmpPath = modelPath + ".downloading";
                    using (var netStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(ggmlType, QuantizationType.NoQuantization, ct).ConfigureAwait(false))
                    using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await netStream.CopyToAsync(fs, ct).ConfigureAwait(false);
                    }
                    File.Move(tmpPath, modelPath, overwrite: true);
                    _log?.Invoke($"HCSWhisper: Model '{modelName}' saved ({new FileInfo(modelPath).Length / 1048576.0:F1} MB)", "blue");
                }

                var sw = Stopwatch.StartNew();
                _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions
                {
                    UseGpu = true,
                    UseFlashAttention = false
                });
                _loadedModelName = fileName;

                // Warm up GPU shaders at audio_ctx=512 (~10s window: ~135-165ms latency with zero decoder looping!)
                using var warmupProc = _factory.CreateBuilder()
                    .WithLanguage("en")
                    .WithNoContext()
                    .WithSingleSegment()
                    .WithAudioContextSize(512)
                    .WithMaxTokensPerSegment(16)
                    .WithPrompt(initialPrompt ?? "")
                    .Build();

                float[] warmAudio = new float[16000];
                await foreach (var _ in warmupProc.ProcessAsync(warmAudio, ct).ConfigureAwait(false)) { }

                _log?.Invoke($"HCSWhisper ready: model '{modelName}' loaded on {ActiveBackend} in {sw.ElapsedMilliseconds} ms", "green");
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<(string Text, long ElapsedMs)> TranscribeAsync(float[] samples16k, string prompt, CancellationToken ct = default)
        {
            if (samples16k == null || samples16k.Length < 1600)
                return ("", 0);

            // Quick peak/energy check: skip pure digital silence immediately
            float peak = 0f;
            for (int i = 0; i < samples16k.Length; i++)
            {
                float a = Math.Abs(samples16k[i]);
                if (a > peak) peak = a;
            }
            if (peak < 0.003f)
                return ("", 0);

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_factory == null)
                    return ("", 0);

                // Pad at least 1.0s with gentle trailing silence so very short commands ("Yes", "Chaff") decode cleanly
                float[] input = samples16k;
                if (input.Length < 16000)
                {
                    input = new float[16000];
                    Array.Copy(samples16k, input, samples16k.Length);
                }

                // audio_ctx=512 (~10.2s capacity) avoids positional-embedding repetition loops while running ~2.5x faster than 1500
                double seconds = input.Length / 16000.0;
                int audioCtx = seconds <= 8.5 ? 512 : 1500;

                var sw = Stopwatch.StartNew();
                using var processor = _factory.CreateBuilder()
                    .WithLanguage("en")
                    .WithNoContext()
                    .WithSingleSegment()
                    .WithAudioContextSize(audioCtx)
                    .WithMaxTokensPerSegment(20)
                    .WithNoSpeechThreshold(0.65f)
                    .WithPrompt(prompt ?? "")
                    .Build();

                var sb = new StringBuilder();
                await foreach (var seg in processor.ProcessAsync(input, ct).ConfigureAwait(false))
                {
                    if (seg.NoSpeechProbability < 0.80f && !string.IsNullOrWhiteSpace(seg.Text))
                        sb.Append(seg.Text);
                }

                sw.Stop();
                return (sb.ToString().Trim(), sw.ElapsedMilliseconds);
            }
            finally
            {
                _gate.Release();
            }
        }

        private static (GgmlType Type, string FileName) ResolveModelSpec(string name)
        {
            string n = (name ?? "base.en").Trim().ToLowerInvariant();
            return n switch
            {
                "tiny.en" or "tiny" => (GgmlType.TinyEn, "ggml-tiny.en.bin"),
                "small.en" or "small" => (GgmlType.SmallEn, "ggml-small.en.bin"),
                _ => (GgmlType.BaseEn, "ggml-base.en.bin")
            };
        }

        public void Dispose()
        {
            try { _factory?.Dispose(); } catch { }
            _factory = null;
            _gate.Dispose();
        }
    }
}
