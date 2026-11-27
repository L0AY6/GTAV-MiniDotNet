using System;
using System.IO;
using System.Text;
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

                MiniMenu.Mods.Add(new ModItem
                {
                    Name = "Test Mod (Ready)",
                    IsEnabled = true,
                    TickAction = () => { }
                });
            }

            MiniMenu.Tick();
        }

        public static void NativeInit(ulong hash)
        {
            if (_nativeInit != null) _nativeInit(hash);
        }

        public static void PushInt(int val)
        {
            if (_nativePush64 != null) _nativePush64((ulong)(long)val);
        }

        public static void PushFloat(float val)
        {
            if (_nativePush64 != null)
            {
                uint bits = *(uint*)&val;
                _nativePush64((ulong)bits);
            }
        }

        public static void PushString(string str, out IntPtr ptr)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(str + "\0");
            ptr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            if (_nativePush64 != null) _nativePush64((ulong)ptr);
        }

        public static void NativeCall()
        {
            if (_nativeCall != null) _nativeCall();
        }

        public static void Log(string message)
        {
            try { File.AppendAllText("MiniDotNet_CS.log", $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"); }
            catch { }
        }
    }
}
