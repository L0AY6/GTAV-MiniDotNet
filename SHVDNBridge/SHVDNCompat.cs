using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GTA
{
    public abstract class Script
    {
        public event EventHandler Tick;
        public event EventHandler Aborted;
        public ScriptSettings Settings { get; internal set; }
        public int Interval { get; set; } = 0;
        internal DateTime NextTick = DateTime.MinValue;

        public string Name => GetType().Name;
        public string Filename { get; internal set; }

        internal void DoTick()
        {
            if (Interval > 0 && DateTime.Now < NextTick) return;
            Tick?.Invoke(this, EventArgs.Empty);
            if (Interval > 0) NextTick = DateTime.Now.AddMilliseconds(Interval);
        }

        internal void DoAbort()
        {
            Aborted?.Invoke(this, EventArgs.Empty);
        }

        public static void Wait(int ms)
        {
            System.Threading.Thread.Sleep(ms);
        }

        public static void Yield()
        {
            System.Threading.Thread.Sleep(1);
        }
    }

    public class ScriptSettings
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static ScriptSettings Load(string filename)
        {
            var settings = new ScriptSettings();
            if (!File.Exists(filename)) return settings;

            string section = "";
            foreach (var rawLine in File.ReadAllLines(filename))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith(";") || line.StartsWith("#")) continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                }
                else
                {
                    int idx = line.IndexOf('=');
                    if (idx > 0)
                    {
                        string k = $"{section}.{line.Substring(0, idx).Trim()}";
                        string v = line.Substring(idx + 1).Trim();
                        settings._values[k] = v;
                    }
                }
            }
            return settings;
        }

        public T GetValue<T>(string section, string name, T defaultValue)
        {
            string key = $"{section}.{name}";
            if (_values.TryGetValue(key, out string raw))
            {
                try
                {
                    if (typeof(T).IsEnum)
                        return (T)Enum.Parse(typeof(T), raw, true);
                    return (T)Convert.ChangeType(raw, typeof(T));
                }
                catch { return defaultValue; }
            }
            return defaultValue;
        }
    }
}

namespace GTA.Native
{
    public struct InputArgument
    {
        public ulong Data;

        public InputArgument(ulong value) { Data = value; }
        public InputArgument(int value) { Data = (ulong)(long)value; }
        public InputArgument(uint value) { Data = value; }
        public InputArgument(bool value) { Data = value ? 1UL : 0UL; }
        public unsafe InputArgument(float value) { Data = *(uint*)&value; }
        public InputArgument(string value)
        {
            IntPtr ptr = Marshal.StringToHGlobalAnsi(value);
            Data = (ulong)ptr;
        }

        public static implicit operator InputArgument(int v) => new InputArgument(v);
        public static implicit operator InputArgument(uint v) => new InputArgument(v);
        public static implicit operator InputArgument(ulong v) => new InputArgument(v);
        public static implicit operator InputArgument(bool v) => new InputArgument(v);
        public static implicit operator InputArgument(float v) => new InputArgument(v);
        public static implicit operator InputArgument(string v) => new InputArgument(v);
    }

    public static unsafe class Function
    {
        internal static delegate* unmanaged[Cdecl]<ulong, void> NativeInit;
        internal static delegate* unmanaged[Cdecl]<ulong, void> NativePush64;
        internal static delegate* unmanaged[Cdecl]<ulong*> NativeCall;

        public static void Call(ulong hash, params InputArgument[] arguments)
        {
            if (NativeInit == null) return;
            NativeInit(hash);
            foreach (var arg in arguments)
                NativePush64(arg.Data);
            NativeCall();
        }

        public static T Call<T>(ulong hash, params InputArgument[] arguments) where T : unmanaged
        {
            if (NativeInit == null) return default;
            NativeInit(hash);
            foreach (var arg in arguments)
                NativePush64(arg.Data);
            ulong* res = NativeCall();
            if (res == null) return default;
            return *(T*)res;
        }
    }
}
