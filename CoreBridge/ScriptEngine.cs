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
        public static void SetupNatives(IntPtr initPtr, IntPtr pushPtr, IntPtr callPtr)
        {
            _nativeInit = (delegate* unmanaged[Cdecl]<ulong, void>)initPtr;
            _nativePush64 = (delegate* unmanaged[Cdecl]<ulong, void>)pushPtr;
            _nativeCall = (delegate* unmanaged[Cdecl]<ulong*>)callPtr;
            Log("[C# AOT Bridge] Native pointers received successfully!");
        }

        public static ulong CallNative(ulong hash, params ulong[] args)
        {
            if (_nativeInit == null || _nativePush64 == null || _nativeCall == null)
                return 0;

            _nativeInit(hash);
            for (int i = 0; i < args.Length; i++)
            {
                _nativePush64(args[i]);
            }
            ulong* result = _nativeCall();
            return result != null ? *result : 0;
        }

        [UnmanagedCallersOnly(EntryPoint = "OnTick")]
        public static void OnTick()
        {
            if (!_initialized)
            {
                _initialized = true;
                Log("=== MiniDotNet C# AOT Engine Active Inside GTA V! ===");
            }
        }

        public static void Log(string message)
        {
            try
            {
                File.AppendAllText("MiniDotNet_CS.log", $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
