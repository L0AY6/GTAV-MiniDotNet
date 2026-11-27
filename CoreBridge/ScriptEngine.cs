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
