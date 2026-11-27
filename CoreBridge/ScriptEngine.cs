using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MiniDotNet.Core
{
    public class ModEntry
    {
        public string Name { get; set; } = "";
        public string FilePath { get; set; } = "";
        public bool IsEnabled { get; set; } = true;
        public bool HasError { get; set; } = false;
    }

    public static unsafe class ScriptEngine
    {
        private static bool _initialized = false;
        private static int _waitFrames = 0;
        private static bool _menuOpen = false;
        private static int _selectedIndex = 0;
        private static int _lastInputTime = 0;
        private static readonly List<ModEntry> _mods = new List<ModEntry>();

        private static delegate* unmanaged[Cdecl]<ulong, void> _nativeInit;
        private static delegate* unmanaged[Cdecl]<ulong, void> _nativePush64;
        private static delegate* unmanaged[Cdecl]<ulong*> _nativeCall;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private static readonly IntPtr[] _strRing = InitRing();
        private static int _ringIdx = 0;

        private static IntPtr[] InitRing()
        {
            var arr = (IntPtr[])Array.CreateInstance(typeof(IntPtr), 32);
            for (int i = 0; i < 32; i++) arr[i] = Marshal.AllocHGlobal(128);
            return arr;
        }

        private static ulong AllocStr(string s)
        {
            IntPtr buf = _strRing[_ringIdx];
            _ringIdx = (_ringIdx + 1) & 31;
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(s ?? "");
            int len = System.Math.Min(bytes.Length, 126);
            Marshal.Copy(bytes, 0, buf, len);
            ((byte*)buf)[len] = 0;
            return (ulong)buf;
        }

        [UnmanagedCallersOnly(EntryPoint = "SetupNatives")]
        public static void SetupNatives(IntPtr initPtr, IntPtr pushPtr, IntPtr callPtr, IntPtr getAddrPtr, IntPtr getAllVehPtr)
        {
            _nativeInit = (delegate* unmanaged[Cdecl]<ulong, void>)initPtr;
            _nativePush64 = (delegate* unmanaged[Cdecl]<ulong, void>)pushPtr;
            _nativeCall = (delegate* unmanaged[Cdecl]<ulong*>)callPtr;
        }

        private static ulong CallNative(ulong hash, params ulong[] args)
        {
            if (_nativeInit == null || _nativePush64 == null || _nativeCall == null) return 0;
            _nativeInit(hash);
            for (int i = 0; i < args.Length; i++) _nativePush64(args[i]);
            ulong* r = _nativeCall();
            return r != null ? *r : 0;
        }

        private static ulong F2U(float f) => *(uint*)&f;

        private static void ScanMods()
        {
            _mods.Clear();
            string scriptsDir = Path.Combine(Directory.GetCurrentDirectory(), "scripts");
            if (!Directory.Exists(scriptsDir)) return;

            foreach (string file in Directory.GetFiles(scriptsDir))
            {
                string name = Path.GetFileName(file);
                if (name.Equals("MiniDotNet.Core.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("ScriptHookVDotNet3.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("System.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("System.Core.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Microsoft.CSharp.dll", StringComparison.OrdinalIgnoreCase)) continue;

                if (name.EndsWith(".mod.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    bool err = new FileInfo(file).Length == 0;
                    _mods.Add(new ModEntry
                    {
                        Name = Path.GetFileNameWithoutExtension(name).Replace(".mod", ""),
                        FilePath = file,
                        IsEnabled = !err,
                        HasError = err
                    });
                }
            }

            if (_mods.Count == 0)
            {
                _mods.Add(new ModEntry { Name = "No Mods Found in /scripts", IsEnabled = false, HasError = false });
            }
        }
                [UnmanagedCallersOnly(EntryPoint = "OnTick")]
        public static void OnTick()
        {
            if (_waitFrames < 200)
            {
                _waitFrames++;
                return;
            }

            if (!_initialized)
            {
                _initialized = true;
                ScanMods();
            }

            int now = (int)CallNative(0x9CD27B0045628463);

            bool f2Key = (GetAsyncKeyState(0x71) & 0x8000) != 0;
            bool rb = CallNative(0xF3A21BCD95725A4A, 2, 206) != 0 || CallNative(0xF3A21BCD95725A4A, 0, 44) != 0;
            bool crossOrA = CallNative(0xF3A21BCD95725A4A, 2, 201) != 0 || CallNative(0xF3A21BCD95725A4A, 0, 18) != 0;
            bool dpadDown = CallNative(0x580417101DDB492F, 2, 173) != 0 || CallNative(0x580417101DDB492F, 0, 19) != 0;

            if ((f2Key || (rb && crossOrA && dpadDown)) && now - _lastInputTime > 350)
            {
                _menuOpen = !_menuOpen;
                _lastInputTime = now;
                if (_menuOpen) ScanMods();
            }

            if (_menuOpen)
            {
                DisableGameDpadAndPhone();
                HandleMenuInput(now);
                DrawMenu();
            }
        }

        private static void DisableGameDpadAndPhone()
        {
            int[] controlsToBlock = {
                27, 19, 85, 74, 20, 47, 51, 18, 22, 24, 25, 45,
                172, 173, 174, 175, 201, 202
            };

            for (int i = 0; i < controlsToBlock.Length; i++)
            {
                CallNative(0xFE99B66D079CF6BC, 0, (ulong)controlsToBlock[i], 1);
                CallNative(0xFE99B66D079CF6BC, 2, (ulong)controlsToBlock[i], 1);
            }
        }

        private static bool IsBtnPressed(int ctrl0, int ctrl2)
        {
            return CallNative(0x91AEF906BCA88877, 0, (ulong)ctrl0) != 0 ||
                   CallNative(0x91AEF906BCA88877, 2, (ulong)ctrl2) != 0;
        }

        private static void HandleMenuInput(int now)
        {
            if (now - _lastInputTime < 180 || _mods.Count == 0) return;

            bool up = IsBtnPressed(27, 172);
            bool down = IsBtnPressed(19, 173);
            bool right = IsBtnPressed(74, 175);
            bool select = IsBtnPressed(18, 201);
            bool back = IsBtnPressed(45, 202);

            if (up)
            {
                _selectedIndex = (_selectedIndex - 1 + _mods.Count) % _mods.Count;
                _lastInputTime = now;
            }
            else if (down)
            {
                _selectedIndex = (_selectedIndex + 1) % _mods.Count;
                _lastInputTime = now;
            }
            else if (right)
            {
                var mod = _mods[_selectedIndex];
                if (!mod.HasError && !string.IsNullOrEmpty(mod.FilePath))
                {
                    _menuOpen = false;
                    byte vk = mod.Name.Contains("Suspension", StringComparison.OrdinalIgnoreCase) ? (byte)0x76 : (byte)0x74;
                    keybd_event(vk, 0, 0, UIntPtr.Zero);
                    keybd_event(vk, 0, 2, UIntPtr.Zero);
                }
                _lastInputTime = now;
            }
            else if (select)
            {
                var mod = _mods[_selectedIndex];
                if (!mod.HasError && !string.IsNullOrEmpty(mod.FilePath))
                {
                    mod.IsEnabled = !mod.IsEnabled;
                }
                _lastInputTime = now;
            }
            else if (back)
            {
                _menuOpen = false;
                _lastInputTime = now;
            }
        }

        private static void DrawMenu()
        {
            float x = 0.14f, y = 0.12f, w = 0.22f;

            CallNative(0x3A618A217E5154F0, F2U(x), F2U(y), F2U(w), F2U(0.055f), 95, 35, 165, 235);
            DrawText("by L0AY6", x, y - 0.022f, 0.25f, 220, 220, 220, 255, true);
            DrawText("MiniDotNet", x, y - 0.008f, 0.48f, 255, 255, 255, 255, true);

            float subY = y + 0.038f;
            CallNative(0x3A618A217E5154F0, F2U(x), F2U(subY), F2U(w), F2U(0.022f), 15, 15, 20, 240);
            DrawText("LOADED MODS", x - 0.098f, subY - 0.009f, 0.26f, 180, 140, 255, 255, false);
            DrawText($"{_selectedIndex + 1}/{_mods.Count}", x + 0.075f, subY - 0.009f, 0.26f, 200, 200, 200, 255, false);

            float itemY = subY + 0.026f;
            for (int i = 0; i < _mods.Count; i++)
            {
                var m = _mods[i];
                bool sel = (i == _selectedIndex);
                float rowH = m.HasError ? 0.042f : 0.030f;

                if (sel)
                    CallNative(0x3A618A217E5154F0, F2U(x), F2U(itemY), F2U(w), F2U(rowH), 245, 245, 250, 245);
                else
                    CallNative(0x3A618A217E5154F0, F2U(x), F2U(itemY), F2U(w), F2U(rowH), 20, 20, 25, 215);

                int tr = sel ? 15 : 240, tg = sel ? 15 : 240, tb = sel ? 15 : 240;
                DrawText(m.Name, x - 0.098f, itemY - (m.HasError ? 0.016f : 0.011f), 0.30f, tr, tg, tb, 255, false);

                if (m.HasError)
                {
                    DrawText("Error", x - 0.098f, itemY + 0.002f, 0.25f, 255, 60, 60, 255, false);
                }
                else if (!string.IsNullOrEmpty(m.FilePath))
                {
                    string status = m.IsEnabled ? "ON" : "OFF";
                    int sr = m.IsEnabled ? 50 : 220, sg = m.IsEnabled ? 210 : 70, sb = m.IsEnabled ? 100 : 70;
                    DrawText(status, x + 0.075f, itemY - 0.011f, 0.28f, sr, sg, sb, 255, false);
                }

                itemY += rowH;
            }
        }

        private static void DrawText(string text, float x, float y, float scale, int r, int g, int b, int a, bool center)
        {
            CallNative(0x25FBB336DF1804CB, AllocStr("STRING"));
            CallNative(0x66E0276CC5F6B9DA, 0);
            CallNative(0x07C837F9A01C34C9, F2U(scale), F2U(scale));
            CallNative(0xBE6B23FFA53FB442, (ulong)r, (ulong)g, (ulong)b, (ulong)a);
            if (center) CallNative(0xC02F4DBFB51D988B, 1);
            CallNative(0x6C188BE134E074AA, AllocStr(text));
            CallNative(0xCD015E5BB0D96A57, F2U(x), F2U(y));
        }
    }
}
