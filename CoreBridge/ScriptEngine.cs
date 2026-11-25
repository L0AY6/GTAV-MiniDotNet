using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace MiniDotNet.Core
{
    public static class ScriptEngine
    {
        private static bool _initialized = false;
        private static readonly List<MethodInfo> _scriptTickMethods = new List<MethodInfo>();

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
            Log("=== MiniDotNet C# Engine (.NET 8) Initialized Successfully! ===");
            
            string scriptsPath = Path.Combine(Directory.GetCurrentDirectory(), "scripts");
            if (!Directory.Exists(scriptsPath))
            {
                Directory.CreateDirectory(scriptsPath);
                return;
            }

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

        private static void Log(string message)
        {
            try
            {
                File.AppendAllText("MiniDotNet_CS.log", $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
