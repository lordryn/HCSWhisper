using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;

namespace HCSWhisper
{
    /// <summary>
    /// Pure zero-dependency VoiceAttack 2 v4 Plugin Shim (HCSWhisper.dll).
    /// References ONLY base .NET 8 BCL types so VoiceAttack's startup Assembly.GetTypes() scan
    /// always succeeds immediately without ReflectionTypeLoadException, then loads HCSWhisper.Core.dll
    /// and native Vulkan/GGML runtimes from %LOCALAPPDATA%\HCSWhisper\engine.
    /// </summary>
    public class HcsWhisperPlugin
    {
        private const string BundleVersion = "1.2.0";
        private static readonly object InitLock = new object();
        private static Type _coreRuntimeType;
        private static MethodInfo _invokeMethod;
        private static MethodInfo _shutdownMethod;
        private static MethodInfo _stopCmdMethod;

        public static string VA_DisplayName() => "HCSWhisper AI Speech Engine";

        public static string VA_DisplayInfo() =>
            "HCSWhisper v1.2.0 — GPU-accelerated Whisper speech engine for HCS Voice Packs (Star Citizen, Elite Dangerous & more).";

        public static Guid VA_Id() => new Guid("{8E47C9A1-5F2B-4D8E-9A31-7C64E0B51F29}");

        public static void VA_StopCommand()
        {
            try { _stopCmdMethod?.Invoke(null, null); } catch { }
        }

        public static void VA_Init1(dynamic vaProxy)
        {
            try
            {
                EnsureCoreLoaded(vaProxy);
            }
            catch (Exception ex)
            {
                try { vaProxy.WriteToLog("HCSWhisper init error: " + ex.Message, "red"); } catch { }
            }
        }

        public static void VA_Invoke1(dynamic vaProxy)
        {
            try
            {
                if (_coreRuntimeType == null)
                    EnsureCoreLoaded(vaProxy);
                _invokeMethod?.Invoke(null, new object[] { (object)vaProxy });
            }
            catch (Exception ex)
            {
                try { vaProxy.WriteToLog("HCSWhisper invoke error: " + ex.Message, "red"); } catch { }
            }
        }

        public static void VA_Exit1(dynamic vaProxy)
        {
            try { _shutdownMethod?.Invoke(null, null); } catch { }
        }

        private static void EnsureCoreLoaded(dynamic vaProxy)
        {
            lock (InitLock)
            {
                if (_coreRuntimeType != null)
                    return;

                string pluginDir = Path.GetDirectoryName(typeof(HcsWhisperPlugin).Assembly.Location);
                if (string.IsNullOrEmpty(pluginDir))
                {
                    string appsDir = null;
                    try { appsDir = (string)vaProxy.AppsDir; } catch { }
                    pluginDir = !string.IsNullOrEmpty(appsDir)
                        ? Path.Combine(appsDir, "HCSWhisper")
                        : Path.Combine(AppContext.BaseDirectory, "Apps", "HCSWhisper");
                }

                string engineDir = ExtractEngineBundle(pluginDir);

                var alc = AssemblyLoadContext.GetLoadContext(typeof(HcsWhisperPlugin).Assembly)
                          ?? AssemblyLoadContext.Default;

                alc.Resolving += (context, asmName) =>
                {
                    if (string.IsNullOrEmpty(asmName?.Name))
                        return null;
                    string p1 = Path.Combine(engineDir, asmName.Name + ".dll");
                    if (File.Exists(p1))
                        return context.LoadFromAssemblyPath(p1);
                    string p2 = Path.Combine(AppContext.BaseDirectory, asmName.Name + ".dll");
                    if (File.Exists(p2))
                        return AssemblyLoadContext.Default.LoadFromAssemblyPath(p2);
                    return null;
                };

                string coreDll = Path.Combine(engineDir, "HCSWhisper.Core.dll");
                Assembly coreAsm = alc.LoadFromAssemblyPath(coreDll);
                _coreRuntimeType = coreAsm.GetType("HCSWhisper.HcsWhisperRuntime", throwOnError: true);

                MethodInfo initMethod = _coreRuntimeType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
                _invokeMethod = _coreRuntimeType.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Static);
                _shutdownMethod = _coreRuntimeType.GetMethod("Shutdown", BindingFlags.Public | BindingFlags.Static);
                _stopCmdMethod = _coreRuntimeType.GetMethod("StopCommand", BindingFlags.Public | BindingFlags.Static);

                initMethod?.Invoke(null, new object[] { (object)vaProxy, pluginDir, engineDir });
            }
        }

        private static string ExtractEngineBundle(string pluginDir)
        {
            string engineDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HCSWhisper", "engine");
            Directory.CreateDirectory(engineDir);

            string pakPath = Path.Combine(pluginDir, "engine.pak");
            string stampPath = Path.Combine(engineDir, ".bundle-version");

            if (File.Exists(pakPath))
            {
                string expectedStamp = $"{BundleVersion}:{new FileInfo(pakPath).Length}";
                bool current = File.Exists(stampPath) &&
                               string.Equals(File.ReadAllText(stampPath).Trim(), expectedStamp, StringComparison.Ordinal) &&
                               File.Exists(Path.Combine(engineDir, "HCSWhisper.Core.dll")) &&
                               File.Exists(Path.Combine(engineDir, "Whisper.net.dll"));

                if (!current)
                {
                    try { Directory.Delete(engineDir, recursive: true); } catch { }
                    Directory.CreateDirectory(engineDir);
                    ZipFile.ExtractToDirectory(pakPath, engineDir, overwriteFiles: true);
                    File.WriteAllText(stampPath, expectedStamp);
                }
            }

            return engineDir;
        }
    }
}
