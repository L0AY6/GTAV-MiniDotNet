using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MiniDotNet.Core
{
    public static unsafe class ScriptEngine
    {
        private static bool _initialized = false;

        private static delegate* unmanaged[Cdecl]<ulong, void> _nativeInit;
        private static delegate* unmanaged[Cdecl]<ulong, void> _nativePush64;
        private static delegate* unmanaged[Cdecl]<ulong*> _nativeCall;

        [UnmanagedCallersOnly(EntryPoint = "SetupNatives")]
        public static void SetupNatives(IntPtr initPtr, IntPtr pushPtr, IntPtr callPtr, IntPtr getAddrPtr, IntPtr getAllVehPtr)
        {
            _nativeInit = (delegate* unmanaged[Cdecl]<ulong, void>)initPtr;
            _nativePush64 = (delegate* unmanaged[Cdecl]<ulong, void>)pushPtr;
            _nativeCall = (delegate* unmanaged[Cdecl]<ulong*>)callPtr;
            Log("[C# AOT Bridge] Core Engine Connected!");
        }

        [UnmanagedCallersOnly(EntryPoint = "OnTick")]
        public static void OnTick()
        {
            if (!_initialized)
            {
                _initialized = true;
                Log("=== MiniDotNet Standalone Core Active! ===");
            }
        }

        public static void Log(string message)
        {
            try { File.AppendAllText("MiniDotNet_CS.log", $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"); }
            catch { }
        }
    }
}
