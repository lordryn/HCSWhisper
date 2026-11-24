using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HCSWhisper
{
    /// <summary>
    /// Lightweight Win32 Keyboard, Mouse, and 16-device USB Joystick/HOTAS poller (32 buttons + POV Hat per stick).
    /// Bypasses locked VoiceAttack profiles completely.
    /// </summary>
    public static class InputPoller
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("winmm.dll")]
        private static extern uint joyGetNumDevs();

        [DllImport("winmm.dll")]
        private static extern uint joyGetPosEx(uint uJoyID, ref JOYINFOEX pji);

        private const uint JOYERR_NOERROR = 0;
        private const uint JOY_RETURNBUTTONS = 0x00000080;
        private const uint JOY_RETURNPOVCTS = 0x00000200;
        private const ushort POV_CENTERED = 0xFFFF;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOYINFOEX
        {
            public uint dwSize;
            public uint dwFlags;
            public uint dwXpos;
            public uint dwYpos;
            public uint dwZpos;
            public uint dwRpos;
            public uint dwUpos;
            public uint dwVpos;
            public uint dwButtons;
            public uint dwButtonNumber;
            public uint dwPOV;
            public uint dwReserved1;
            public uint dwReserved2;
        }

        // Keys safe to poll during interactive capture (excludes LButton/RButton so UI clicks don't bind)
        private static readonly int[] PollableVKeys = BuildPollableKeys();

        private static int[] BuildPollableKeys()
        {
            var list = new List<int>
            {
                0x04, // Middle Mouse (MButton)
                0x05, // Mouse 4 (XButton1)
                0x06, // Mouse 5 (XButton2)
                0x08, // Backspace
                0x09, // Tab
                0x13, // Pause
                0x14, // CapsLock
                0x20, // Space
                0x21, 0x22, 0x23, 0x24, // PgUp, PgDn, End, Home
                0x25, 0x26, 0x27, 0x28, // Arrows
                0x2D, 0x2E, // Insert, Delete
                0x90, 0x91, // NumLock, ScrollLock
                0xA0, 0xA1, // LShift, RShift
                0xA2, 0xA3, // LCtrl, RCtrl
                0xA4, 0xA5, // LAlt (LMenu), RAlt (RMenu)
            };
            for (int k = 0x30; k <= 0x5A; k++) list.Add(k); // 0-9, A-Z
            for (int k = 0x60; k <= 0x87; k++) list.Add(k); // Numpad 0-9, Multiply/Add/etc., F1-F24
            for (int k = 0xBA; k <= 0xDF; k++) list.Add(k); // OEM punctuation (` \ [ ] ; ' , . / etc.)
            return list.ToArray();
        }

        public static bool IsBindingPressed(string binding)
        {
            if (string.IsNullOrWhiteSpace(binding))
                return false;

            if (binding.StartsWith("Key:", StringComparison.OrdinalIgnoreCase))
            {
                string keyPart = binding.Substring(4).Trim();
                if (Enum.TryParse<Keys>(keyPart, true, out Keys key))
                {
                    return (GetAsyncKeyState((int)key) & 0x8000) != 0;
                }
                if (int.TryParse(keyPart, out int rawVk))
                {
                    return (GetAsyncKeyState(rawVk) & 0x8000) != 0;
                }
                return false;
            }

            if (binding.StartsWith("Joy:", StringComparison.OrdinalIgnoreCase))
            {
                // Format: Joy:<joyIndex>:Btn<1..32> or Joy:<joyIndex>:Pov<Up|Right|Down|Left>
                string[] parts = binding.Split(':');
                if (parts.Length < 3 || !uint.TryParse(parts[1], out uint joyId))
                    return false;

                JOYINFOEX info = new JOYINFOEX
                {
                    dwSize = (uint)Marshal.SizeOf<JOYINFOEX>(),
                    dwFlags = JOY_RETURNBUTTONS | JOY_RETURNPOVCTS
                };

                if (joyGetPosEx(joyId, ref info) != JOYERR_NOERROR)
                    return false;

                string target = parts[2];
                if (target.StartsWith("Btn", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(target.Substring(3), out int btnNum) &&
                    btnNum >= 1 && btnNum <= 32)
                {
                    uint mask = 1u << (btnNum - 1);
                    return (info.dwButtons & mask) != 0;
                }

                if (target.StartsWith("Pov", StringComparison.OrdinalIgnoreCase))
                {
                    if ((info.dwPOV & 0xFFFF) == POV_CENTERED || info.dwPOV > 35900)
                        return false;
                    string dir = GetPovDirection(info.dwPOV);
                    return string.Equals("Pov" + dir, target, StringComparison.OrdinalIgnoreCase);
                }
            }

            return false;
        }

        private static string GetPovDirection(uint povHundredths)
        {
            if ((povHundredths & 0xFFFF) == POV_CENTERED || povHundredths > 35900)
                return null;
            // 0 = Up, 9000 = Right, 18000 = Down, 27000 = Left
            if (povHundredths >= 31500 || povHundredths < 4500) return "Up";
            if (povHundredths >= 4500 && povHundredths < 13500) return "Right";
            if (povHundredths >= 13500 && povHundredths < 22500) return "Down";
            return "Left";
        }

        public static string FormatFriendly(string binding)
        {
            if (string.IsNullOrWhiteSpace(binding))
                return "None";

            if (binding.StartsWith("Key:", StringComparison.OrdinalIgnoreCase))
            {
                string k = binding.Substring(4);
                return k switch
                {
                    "RMenu" => "Right Alt",
                    "LMenu" => "Left Alt",
                    "RControlKey" => "Right Ctrl",
                    "LControlKey" => "Left Ctrl",
                    "RShiftKey" => "Right Shift",
                    "LShiftKey" => "Left Shift",
                    "XButton1" => "Mouse 4 (Back)",
                    "XButton2" => "Mouse 5 (Forward)",
                    "MButton" => "Middle Mouse",
                    "Capital" or "CapsLock" => "Caps Lock",
                    "Scroll" => "Scroll Lock",
                    "Oemtilde" => "Tilde (~)",
                    _ => k
                };
            }

            if (binding.StartsWith("Joy:", StringComparison.OrdinalIgnoreCase))
            {
                string[] p = binding.Split(':');
                if (p.Length >= 3 && int.TryParse(p[1], out int jIdx))
                {
                    string item = p[2].StartsWith("Btn", StringComparison.OrdinalIgnoreCase)
                        ? "Button " + p[2].Substring(3)
                        : "Hat " + p[2].Substring(3);
                    return $"HOTAS / Stick {jIdx + 1} — {item}";
                }
            }

            return binding;
        }

        /// <summary>
        /// Waits for the user to press any Keyboard key, Mouse 3/4/5 button, or HOTAS button/POV hat.
        /// </summary>
        public static async Task<string> CaptureNextBindingAsync(CancellationToken ct)
        {
            // Snapshot baseline pressed state so held keys/buttons must be newly pressed
            await Task.Delay(150, ct).ConfigureAwait(false);
            uint maxDevs = Math.Min(16u, joyGetNumDevs());

            var baseKeys = new HashSet<int>();
            foreach (int vk in PollableVKeys)
            {
                if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                    baseKeys.Add(vk);
            }

            uint[] baseJoyBtns = new uint[maxDevs];
            string[] baseJoyPov = new string[maxDevs];
            for (uint j = 0; j < maxDevs; j++)
            {
                JOYINFOEX info = new JOYINFOEX { dwSize = (uint)Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNBUTTONS | JOY_RETURNPOVCTS };
                if (joyGetPosEx(j, ref info) == JOYERR_NOERROR)
                {
                    baseJoyBtns[j] = info.dwButtons;
                    baseJoyPov[j] = GetPovDirection(info.dwPOV);
                }
            }

            while (!ct.IsCancellationRequested)
            {
                // Escape cancels binding
                if ((GetAsyncKeyState((int)Keys.Escape) & 0x8000) != 0)
                    return null;

                foreach (int vk in PollableVKeys)
                {
                    bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
                    if (!down)
                    {
                        baseKeys.Remove(vk);
                    }
                    else if (!baseKeys.Contains(vk))
                    {
                        Keys keyEnum = (Keys)vk;
                        return "Key:" + keyEnum.ToString();
                    }
                }

                for (uint j = 0; j < maxDevs; j++)
                {
                    JOYINFOEX info = new JOYINFOEX { dwSize = (uint)Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNBUTTONS | JOY_RETURNPOVCTS };
                    if (joyGetPosEx(j, ref info) != JOYERR_NOERROR)
                        continue;

                    uint newlyPressed = info.dwButtons & ~baseJoyBtns[j];
                    baseJoyBtns[j] = info.dwButtons;
                    if (newlyPressed != 0)
                    {
                        for (int b = 1; b <= 32; b++)
                        {
                            if ((newlyPressed & (1u << (b - 1))) != 0)
                                return $"Joy:{j}:Btn{b}";
                        }
                    }

                    string pov = GetPovDirection(info.dwPOV);
                    if (pov != null && pov != baseJoyPov[j])
                        return $"Joy:{j}:Pov{pov}";
                    baseJoyPov[j] = pov;
                }

                await Task.Delay(15, ct).ConfigureAwait(false);
            }

            return null;
        }
    }
}
