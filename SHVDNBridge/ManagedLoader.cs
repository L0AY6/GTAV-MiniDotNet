using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using Microsoft.CSharp;
using GTA.Native;

namespace GTA
{
    public class ManagedModEntry
    {
        public string Name;
        public Script Instance;
        public bool IsEnabled = true;
        public bool HasError = false;
        public string ErrorText = "";
    }

    public static unsafe class ManagedLoader
    {
        public static List<ManagedModEntry> LoadedMods = new List<ManagedModEntry>();

        public static int Initialize(string args)
        {
            try
            {
                string[] ptrs = args.Split('|');
                Function.NativeInit = (delegate* unmanaged[Cdecl]<ulong, void>)new IntPtr(long.Parse(ptrs));
                Function.NativePush64 = (delegate* unmanaged[Cdecl]<ulong, void>)new IntPtr(long.Parse(ptrs));
                Function.NativeCall = (delegate* unmanaged[Cdecl]<ulong*>)new IntPtr(long.Parse(ptrs));

                LoadAllFromScriptsFolder();
                return 1;
            }
            catch (Exception ex)
            {
                Log($"[Init Error] {ex.Message}");
                return 0;
            }
        }

        public static void LoadAllFromScriptsFolder()
        {
            string scriptsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts");
            if (!Directory.Exists(scriptsDir)) Directory.CreateDirectory(scriptsDir);

            // 1. Load pre-compiled .dll mods
            foreach (string dll in Directory.GetFiles(scriptsDir, "*.dll"))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.StartsWith("MiniDotNet", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("ScriptHookV", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    Assembly asm = Assembly.LoadFrom(dll);
                    InstantiateScripts(asm, dll);
                }
                catch (Exception ex)
                {
                    LoadedMods.Add(new ManagedModEntry { Name = name, IsEnabled = false, HasError = true, ErrorText = ex.Message });
                    Log($"[DLL Load Error] {name}: {ex.Message}");
                }
            }

            // 2. Compile and run .cs text scripts
            foreach (string cs in Directory.GetFiles(scriptsDir, "*.cs"))
            {
                CompileAndLoadCS(cs);
            }
        }

        private static void CompileAndLoadCS(string csPath)
        {
            string name = Path.GetFileNameWithoutExtension(csPath);
            try
            {
                using (var provider = new CSharpCodeProvider())
                {
                    var cp = new CompilerParameters { GenerateInMemory = true, WarningLevel = 0 };
                    cp.ReferencedAssemblies.Add("System.dll");
                    cp.ReferencedAssemblies.Add("System.Core.dll");
                    cp.ReferencedAssemblies.Add("System.Drawing.dll");
                    cp.ReferencedAssemblies.Add("System.Windows.Forms.dll");
                    cp.ReferencedAssemblies.Add(Assembly.GetExecutingAssembly().Location);

                    CompilerResults cr = provider.CompileAssemblyFromFile(cp, csPath);
                    if (cr.Errors.HasErrors)
                    {
                        string firstErr = cr.Errors.ErrorText;
                        LoadedMods.Add(new ManagedModEntry { Name = name, IsEnabled = false, HasError = true, ErrorText = firstErr });
                        Log($"[CS Compile Error] {name}: {firstErr}");
                        return;
                    }

                    InstantiateScripts(cr.CompiledAssembly, csPath);
                }
            }
            catch (Exception ex)
            {
                LoadedMods.Add(new ManagedModEntry { Name = name, IsEnabled = false, HasError = true, ErrorText = ex.Message });
                Log($"[CS Error] {name}: {ex.Message}");
            }
        }

        private static void InstantiateScripts(Assembly asm, string filePath)
        {
            string iniPath = Path.ChangeExtension(filePath, ".ini");
            var scriptTypes = asm.GetTypes().Where(t => typeof(Script).IsAssignableFrom(t) && !t.IsAbstract);

            foreach (var type in scriptTypes)
            {
                try
                {
                    Script instance = (Script)Activator.CreateInstance(type);
                    instance.Filename = filePath;
                    instance.Settings = ScriptSettings.Load(iniPath);

                    LoadedMods.Add(new ManagedModEntry { Name = type.Name, Instance = instance, IsEnabled = true });
                    Log($"[Loaded] {type.Name} from {Path.GetFileName(filePath)}");
                }
                catch (Exception ex)
                {
                    LoadedMods.Add(new ManagedModEntry { Name = type.Name, IsEnabled = false, HasError = true, ErrorText = ex.Message });
                    Log($"[Instance Error] {type.Name}: {ex.Message}");
                }
            }
        }

        public static int Tick(string unused)
        {
            foreach (var mod in LoadedMods)
            {
                if (!mod.IsEnabled || mod.HasError || mod.Instance == null) continue;
                try
                {
                    mod.Instance.DoTick();
                }
                catch (Exception ex)
                {
                    mod.IsEnabled = false;
                    mod.HasError = true;
                    mod.ErrorText = ex.Message;
                    Log($"[Runtime Error] {mod.Name}: {ex.Message}");
                }
            }
            return 1;
        }

        private static void Log(string msg)
        {
            try { File.AppendAllText("MiniDotNet_Bridge.log", $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}"); }
            catch { }
        }
    }
}
