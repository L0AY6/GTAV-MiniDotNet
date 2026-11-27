using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.CodeDom.Compiler;
using Microsoft.CSharp;

namespace GTA
{
    public abstract class Script
    {
        public string BaseDirectory => Path.Combine(Directory.GetCurrentDirectory(), "scripts");
        public event EventHandler Tick;
        public void OnTickInternal() { Tick?.Invoke(this, EventArgs.Empty); }
    }

    public static class MonoBridgeHost
    {
        private static readonly List<Script> _scripts = new List<Script>();
        private static bool _loaded = false;

        [DllImport("MiniDotNet.asi", CallingConvention = CallingConvention.Cdecl)]
        public static extern void BridgeNativeInit(ulong hash);

        [DllImport("MiniDotNet.asi", CallingConvention = CallingConvention.Cdecl)]
        public static extern void BridgeNativePush64(ulong val);

        [DllImport("MiniDotNet.asi", CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong BridgeNativeCall();

        public static void InitializeAll()
        {
            if (_loaded) return;
            _loaded = true;
            string scriptsDir = Path.Combine(Directory.GetCurrentDirectory(), "scripts");
            if (!Directory.Exists(scriptsDir)) return;

            Log("=== Mono Bridge Starting ===");

            // 1. تحميل ملفات .dll الجاهزة
            foreach (string dll in Directory.GetFiles(scriptsDir, "*.dll"))
            {
                string name = Path.GetFileName(dll);
                if (name.Equals("MiniDotNet.Core.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("ScriptHookVDotNet3.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".mod.dll", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    Assembly asm = Assembly.LoadFrom(dll);
                    RegisterScriptsFromAssembly(asm, name);
                }
                catch (Exception ex) { Log($"[DLL Error] {name}: {ex.Message}"); }
            }

            // 2. ترجمة وتشغيل ملفات .cs النصية مباشرة
            string[] csFiles = Directory.GetFiles(scriptsDir, "*.cs");
            if (csFiles.Length > 0)
            {
                try
                {
                    var provider = new CSharpCodeProvider();
                    var parameters = new CompilerParameters { GenerateInMemory = true, GenerateExecutable = false };
                    parameters.ReferencedAssemblies.Add("System.dll");
                    parameters.ReferencedAssemblies.Add("System.Core.dll");
                    parameters.ReferencedAssemblies.Add(Assembly.GetExecutingAssembly().Location);

                    CompilerResults results = provider.CompileAssemblyFromFile(parameters, csFiles);
                    if (results.Errors.HasErrors)
                    {
                        foreach (CompilerError err in results.Errors) Log($"[CS Compile Error] {err.ErrorText}");
                    }
                    else
                    {
                        RegisterScriptsFromAssembly(results.CompiledAssembly, "RuntimeScripts.cs");
                    }
                }
                catch (Exception ex) { Log($"[CS Host Error] {ex.Message}"); }
            }
        }

        private static void RegisterScriptsFromAssembly(Assembly asm, string sourceName)
        {
            foreach (Type t in asm.GetTypes())
            {
                if (t.IsSubclassOf(typeof(Script)) && !t.IsAbstract)
                {
                    try
                    {
                        Script s = (Script)Activator.CreateInstance(t);
                        _scripts.Add(s);
                        Log($"[Mono Loaded] {t.Name} from {sourceName}");
                    }
                    catch (Exception ex) { Log($"[Init Error] {t.Name}: {ex.Message}"); }
                }
            }
        }

        public static void TickAll()
        {
            if (!_loaded) InitializeAll();
            for (int i = 0; i < _scripts.Count; i++)
            {
                try { _scripts[i].OnTickInternal(); }
                catch { }
            }
        }

        public static void Log(string msg)
        {
            try { File.AppendAllText("MiniDotNet_Mono.log", $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}"); }
            catch { }
        }
    }
}
