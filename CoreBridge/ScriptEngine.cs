using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace MiniDotNet.Core
{
    public static unsafe class ScriptEngine
    {
        private static bool _initialized = false;
        private static readonly List<MethodInfo> _scriptTickMethods = new List<MethodInfo>();

        private static delegate* unmanaged[Cdecl]<ulong, void> _nativeInit;
        private static delegate* unmanaged[Cdecl]<ulong, void> _nativePush64;
        private static delegate* unmanaged[Cdecl]<ulong*> _nativeCall;

        [UnmanagedCallersOnly]
        public static void SetupNatives(IntPtr initPtr, IntPtr pushPtr, IntPtr callPtr)
        {
            _nativeInit = (delegate* unmanaged[Cdecl]<ulong, void>)initPtr;
            _nativePush64 = (delegate* unmanaged[Cdecl]<ulong, void>)pushPtr;
            _nativeCall = (delegate* unmanaged[Cdecl]<ulong*>)callPtr;
            Log("[C# Bridge] Native pointers received successfully!");
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

        [UnmanagedCallersOnly]
        public static void OnTick()
        {
            if (!_initialized)
            {
                _initialized = true;
                InitializeScripts();
            }

            foreach (var tickMethod in _scriptTickMethods)
            {
                try
                {
                    tickMethod.Invoke(null, null);
                }
                catch (Exception ex)
                {
                    Log($"[Script Error] {ex.InnerException?.Message ?? ex.Message}");
                }
            }
        }

        private static void InitializeScripts()
        {
            Log("=== MiniDotNet C# Engine (.NET 8) Active Inside GTA V! ===");
            
            string scriptsPath = Path.Combine(Directory.GetCurrentDirectory(), "scripts");
            if (!Directory.Exists(scriptsPath)) return;

            foreach (string dllFile in Directory.GetFiles(scriptsPath, "*.dll"))
            {
                string fileName = Path.GetFileName(dllFile);
                if (fileName.Equals("MiniDotNet.Core.dll", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    Assembly asm = Assembly.LoadFrom(dllFile);
                    foreach (Type type in asm.GetTypes())
                    {
                        MethodInfo? tick = type.GetMethod("OnTick", BindingFlags.Public | BindingFlags.Static);
                        if (tick != null)
                        {
                            _scriptTickMethods.Add(tick);
                            Log($"[Loaded Script] {fileName} -> {type.Name}.OnTick()");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[Load Failed] {fileName}: {ex.Message}");
                }
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
