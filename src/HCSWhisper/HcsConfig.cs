using System;
using System.IO;
using System.Text.Json;

namespace HCSWhisper
{
    public sealed class HcsConfig
    {
        public string Mode { get; set; } = "PTT"; // "PTT" or "OpenMic"
        public string PttBinding { get; set; } = "Key:RMenu"; // Default: Right Alt
        public string SuppressBinding { get; set; } = ""; // Optional Discord PTT / Mute key in OpenMic mode
        public string ModelName { get; set; } = "base.en"; // "tiny.en", "base.en", "small.en"
        public string MicDeviceId { get; set; } = ""; // Empty = Windows Default Recording Device
        public double FuzzyThreshold { get; set; } = 0.78;
        public float VadRmsThreshold { get; set; } = 0.015f;
        public int VadSilenceHangoverMs { get; set; } = 420;
        public bool AutoMuteVaSpeech { get; set; } = true;
        public bool PlayPttBeep { get; set; } = false;
        public bool Paused { get; set; } = false;

        public static string DataDir
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HCSWhisper");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string ModelsDir
        {
            get
            {
                string dir = Path.Combine(DataDir, "models");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string ConfigFilePath => Path.Combine(DataDir, "config.json");

        public static HcsConfig Load()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    string json = File.ReadAllText(ConfigFilePath);
                    var loaded = JsonSerializer.Deserialize<HcsConfig>(json);
                    if (loaded != null)
                        return loaded;
                }
            }
            catch
            {
                // Return defaults if config is corrupted
            }

            var cfg = new HcsConfig();
            cfg.Save();
            return cfg;
        }

        public void Save()
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(ConfigFilePath, JsonSerializer.Serialize(this, opts));
            }
            catch
            {
                // Ignore transient write lock errors
            }
        }
    }
}
