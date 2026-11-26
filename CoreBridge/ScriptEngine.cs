using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using GTA;

namespace MiniDotNet.Core
{
    public static unsafe class ScriptEngine
    {
        private static bool _initialized = false;
        private static readonly List<Script> _activeScripts = new List<Script>();

        private static delegate* unmanaged[Cdecl]<ulong, void> _nativeInit;
        private static delegate* unmanaged[Cdecl]<ulong, void> _nativePush64;
        private static delegate* unmanaged[Cdecl]<ulong*> _nativeCall;
        private static delegate* unmanaged[Cdecl]<int, IntPtr> _getEntityAddr;
        private static delegate* unmanaged[Cdecl]<int[], int, int> _worldGetAllVeh;

        [UnmanagedCallersOnly(EntryPoint = "SetupNatives")]
        public static void SetupNatives(IntPtr initPtr, IntPtr pushPtr, IntPtr callPtr, IntPtr getAddrPtr, IntPtr getAllVehPtr)
        {
            _nativeInit = (delegate* unmanaged[Cdecl]<ulong, void>)initPtr;
            _nativePush64 = (delegate* unmanaged[Cdecl]<ulong, void>)pushPtr;
            _nativeCall = (delegate* unmanaged[Cdecl]<ulong*>)callPtr;
            _getEntityAddr = (delegate* unmanaged[Cdecl]<int, IntPtr>)getAddrPtr;
            _worldGetAllVeh = (delegate* unmanaged[Cdecl]<int[], int, int>)getAllVehPtr;
            Log("[C# AOT Bridge] All Native & Memory pointers bound!");
        }

        public static ulong CallNative(ulong hash, params ulong[] args)
        {
            if (_nativeInit == null || _nativePush64 == null || _nativeCall == null) return 0;
            _nativeInit(hash);
            for (int i = 0; i < args.Length; i++) _nativePush64(args[i]);
            ulong* result = _nativeCall();
            return result != null ? *result : 0;
        }

        public static IntPtr GetEntityAddress(int handle)
        {
            return _getEntityAddr != null ? _getEntityAddr(handle) : IntPtr.Zero;
        }

        public static int[] GetAllVehicleHandles()
        {
            if (_worldGetAllVeh == null) return Array.Empty<int>();
            int[] buffer = new int[256];
            int count = _worldGetAllVeh(buffer, 256);
            if (count <= 0) return Array.Empty<int>();
            int[] result = new int[count];
            Array.Copy(buffer, result, count);
            return result;
        }

        [UnmanagedCallersOnly(EntryPoint = "OnTick")]
        public static void OnTick()
        {
            if (!_initialized)
            {
                _initialized = true;
                ScanExternalScripts();
            }

            for (int i = 0; i < _activeScripts.Count; i++)
            {
                try { _activeScripts[i].OnTickInternal(); }
                catch (Exception ex) { Log($"[Tick Error] {ex.Message}"); }
            }
        }

        private static void ScanExternalScripts()
        {
            Log("=== MiniDotNet Standalone Wrapper Ready! ===");
            string scriptsDir = Path.Combine(Directory.GetCurrentDirectory(), "scripts");
            if (!Directory.Exists(scriptsDir)) return;

            string[] csFiles = Directory.GetFiles(scriptsDir, "*.cs");
            Log($"[Scanner] Found {csFiles.Length} external .cs script(s) in scripts folder.");
        }

        public static void Log(string message)
        {
            try { File.AppendAllText("MiniDotNet_CS.log", $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"); }
            catch { }
        }
    }
}
