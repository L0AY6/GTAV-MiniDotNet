using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.CodeDom.Compiler;
using Microsoft.CSharp;

namespace System.Windows.Forms
{
    public enum Keys { None = 0, Back = 8, ShiftKey = 16, ControlKey = 17, Menu = 18, Home = 36, Left = 37, Up = 38, Right = 39, Down = 40, F2 = 113, F4 = 115, F7 = 118, F8 = 119, F9 = 120 }
    public static class Cursor
    {
        [DllImport("user32.dll")] static extern bool GetCursorPos(out System.Drawing.Point lpPoint);
        public static System.Drawing.Point Position { get { GetCursorPos(out var p); return p; } }
    }
}

namespace System.Drawing { public struct Point { public int X; public int Y; } }

namespace GTA.Math
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Vector3
    {
        public float X, Y, Z;
        public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.X * d, a.Y * d, a.Z * d);
    }
}

namespace GTA.Native
{
    public enum Hash : ulong
    {
        PLAYER_PED_ID = 0xD80958FC74E988A6, IS_PED_IN_ANY_VEHICLE = 0x997ABD671D25CA0B,
        GET_VEHICLE_PED_IS_IN = 0x9A9112A0FE9A4713, DOES_ENTITY_EXIST = 0x7239B21A38F536BA,
        GET_ENTITY_MODEL = 0x9F47B058362C84B5, GET_VEHICLE_CLASS = 0x29439776AAA00A62,
        GET_DISPLAY_NAME_FROM_VEHICLE_MODEL = 0xB215AAC32D25D019, SET_VEHICLE_ON_GROUND_PROPERLY = 0x49733E92263139D1,
        IS_CONTROL_JUST_PRESSED = 0x580417101DDB492F, IS_CONTROL_PRESSED = 0xF3A21BCD95725A4A,
        DISABLE_CONTROL_ACTION = 0xFE99B66D079CF6BC, SET_MOUSE_CURSOR_THIS_FRAME = 0xAAE7CE1D63167423,
        GET_GAME_TIMER = 0x9CD27B0045628463, DRAW_RECT = 0x3A618A217E5154F0,
        BEGIN_TEXT_COMMAND_DISPLAY_TEXT = 0x25FBB336DF1804CB, SET_TEXT_FONT = 0x66E0276CC5F6B9DA,
        SET_TEXT_SCALE = 0x07C837F9A01C34C9, SET_TEXT_COLOUR = 0xBE6B23FFA53FB442,
        SET_TEXT_CENTRE = 0xC02F4DBFB51D988B, ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME = 0x6C188BE134E074AA,
        END_TEXT_COMMAND_DISPLAY_TEXT = 0xCD015E5BB0D96A57, BEGIN_TEXT_COMMAND_THEFEED_POST = 0x202709F4C58A0424,
        END_TEXT_COMMAND_THEFEED_POST_TICKER = 0x2ED7843F8F801023, BEGIN_TEXT_COMMAND_PRINT = 0xB87A37EEB7FAA67D,
        END_TEXT_COMMAND_PRINT = 0x9D77056A530643F6
    }

    public class InputArgument
    {
        private static readonly IntPtr[] _ring = InitRing();
        private static int _idx = 0;
        private static IntPtr[] InitRing()
        {
            var a = new IntPtr;
            for (int i = 0; i < 64; i++) a[i] = Marshal.AllocHGlobal(256);
            return a;
        }
        public ulong Value { get; }
        public InputArgument(ulong val) { Value = val; }
        public InputArgument(int val) { Value = (ulong)val; }
        public InputArgument(uint val) { Value = val; }
        public InputArgument(bool val) { Value = val ? 1UL : 0UL; }
        public unsafe InputArgument(float val) { Value = *(uint*)&val; }
        public unsafe InputArgument(string val)
        {
            IntPtr b = _ring[_idx]; _idx = (_idx + 1) & 63;
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(val ?? "");
            int len = System.Math.Min(bytes.Length, 254);
            Marshal.Copy(bytes, 0, b, len); ((byte*)b)[len] = 0;
            Value = (ulong)b;
        }
        public static implicit operator InputArgument(int v) => new InputArgument(v);
        public static implicit operator InputArgument(uint v) => new InputArgument(v);
        public static implicit operator InputArgument(ulong v) => new InputArgument(v);
        public static implicit operator InputArgument(float v) => new InputArgument(v);
        public static implicit operator InputArgument(bool v) => new InputArgument(v);
        public static implicit operator InputArgument(string v) => new InputArgument(v);
    }

    public static unsafe class Function
    {
        public static void Call(Hash hash, params InputArgument[] args) => Call((ulong)hash, args);
        public static void Call(ulong hash, params InputArgument[] args)
        {
            GTA.MonoBridgeHost.BridgeNativeInit(hash);
            for (int i = 0; i < args.Length; i++) GTA.MonoBridgeHost.BridgeNativePush64(args[i].Value);
            GTA.MonoBridgeHost.BridgeNativeCall();
        }
        public static T Call<T>(Hash hash, params InputArgument[] args) => Call<T>((ulong)hash, args);
        public static T Call<T>(ulong hash, params InputArgument[] args)
        {
            GTA.MonoBridgeHost.BridgeNativeInit(hash);
            for (int i = 0; i < args.Length; i++) GTA.MonoBridgeHost.BridgeNativePush64(args[i].Value);
            ulong res = GTA.MonoBridgeHost.BridgeNativeCall();
            if (typeof(T) == typeof(int)) return (T)(object)(int)res;
            if (typeof(T) == typeof(bool)) return (T)(object)(res != 0);
            if (typeof(T) == typeof(float)) { float f = *(float*)&res; return (T)(object)f; }
            if (typeof(T) == typeof(string)) return (T)(object)(Marshal.PtrToStringAnsi((IntPtr)res) ?? "");
            return default(T);
        }
    }
}
namespace GTA.UI
{
    using GTA.Native;
    public static class Notification
    {
        public static void PostTicker(string msg, bool imp, bool cache = true)
        {
            Function.Call(Hash.BEGIN_TEXT_COMMAND_THEFEED_POST, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, msg);
            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_TICKER, imp, cache);
        }
    }
    public static class Screen
    {
        public static void ShowSubtitle(string msg, int dur = 2500)
        {
            Function.Call(Hash.BEGIN_TEXT_COMMAND_PRINT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, msg);
            Function.Call(Hash.END_TEXT_COMMAND_PRINT, dur, true);
        }
    }
}

namespace GTA
{
    using GTA.Math;
    using GTA.Native;
    using System.Windows.Forms;

    public enum Control { Attack = 24, Attack2 = 257, Aim = 25, LookLeftRight = 1, LookUpDown = 2, FrontendUp = 172, FrontendDown = 173, FrontendLeft = 174, FrontendRight = 175, FrontendAccept = 201, FrontendX = 203, FrontendY = 204, FrontendLb = 205, FrontendRb = 206, CursorAccept = 237 }
    public enum VehicleClass { Compacts = 0, Sedans = 1, SUVs = 2, Coupes = 3, Muscle = 4, SportsClassics = 5, Sports = 6, Super = 7, Motorcycles = 8, OffRoad = 9, Industrial = 10, Utility = 11, Vans = 12, Cycles = 13, Boats = 14, Helicopters = 15, Planes = 16, Service = 17, Emergency = 18, Military = 19, Commercial = 20, Trains = 21 }
    public struct Model { public int Hash { get; } public Model(int hash) { Hash = hash; } }

    public unsafe class HandlingData
    {
        private readonly IntPtr _ptr;
        public HandlingData(IntPtr ptr) { _ptr = ptr; }
        public bool IsValid => (ulong)_ptr > 0x10000 && (ulong)_ptr < 0x7FFFFFFFFFFF;
        private float R(int o) => IsValid ? *(float*)(_ptr + o) : 0f;
        private void W(int o, float v) { if (IsValid && !float.IsNaN(v) && !float.IsInfinity(v)) *(float*)(_ptr + o) = v; }
        public float Mass { get => R(0x0C); set => W(0x0C, value); }
        public Vector3 InertiaMultiplier { get => new Vector3(R(0x30), R(0x34), R(0x38)); set { } }
        public float SuspensionForce { get => R(0xBC); set => W(0xBC, value); }
        public float SuspensionCompressionDamping { get => R(0xC0); set => W(0xC0, value); }
        public float SuspensionReboundDamping { get => R(0xC4); set => W(0xC4, value); }
        public float SuspensionUpperLimit { get => R(0xC8); set => W(0xC8, value); }
        public float SuspensionLowerLimit { get => R(0xCC); set => W(0xCC, value); }
        public float SuspensionRaise { get => R(0xD0); set => W(0xD0, value); }
        public float AntiRollBarForce { get => R(0xDC); set => W(0xDC, value); }
        public float RollCenterHeightFront { get => R(0xE8); set => W(0xE8, value); }
        public float RollCenterHeightRear { get => R(0xEC); set => W(0xEC, value); }
    }

    public unsafe class Vehicle
    {
        public int Handle { get; }
        public Vehicle(int handle) { Handle = handle; }
        public bool Exists() => Handle != 0 && Function.Call<bool>(Hash.DOES_ENTITY_EXIST, Handle);
        public Model Model => new Model(Function.Call<int>(Hash.GET_ENTITY_MODEL, Handle));
        public VehicleClass ClassType => (VehicleClass)Function.Call<int>(Hash.GET_VEHICLE_CLASS, Handle);
        public string DisplayName => Function.Call<string>(Hash.GET_DISPLAY_NAME_FROM_VEHICLE_MODEL, Model.Hash);
        public void PlaceOnGround() => Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, Handle);
        public HandlingData HandlingData
        {
            get
            {
                IntPtr a = MonoBridgeHost.BridgeGetEntityAddr(Handle);
                if ((ulong)a < 0x10000) return new HandlingData(IntPtr.Zero);
                IntPtr p = *(IntPtr*)(a + 0x960);
                if ((ulong)p < 0x10000 || (ulong)p > 0x7FFFFFFFFFFF) p = *(IntPtr*)(a + 0x938);
                return new HandlingData(p);
            }
        }
    }

    public class Ped
    {
        public int Handle { get; }
        public Ped(int handle) { Handle = handle; }
        public bool Exists() => Handle != 0 && Function.Call<bool>(Hash.DOES_ENTITY_EXIST, Handle);
        public bool IsInVehicle() => Function.Call<bool>(Hash.IS_PED_IN_ANY_VEHICLE, Handle, false);
        public Vehicle CurrentVehicle => IsInVehicle() ? new Vehicle(Function.Call<int>(Hash.GET_VEHICLE_PED_IS_IN, Handle, false)) : new Vehicle(0);
    }

    public class Player { public Ped Character => new Ped(Function.Call<int>(Hash.PLAYER_PED_ID)); }
        public static class Game
    {
        public static Player Player { get; } = new Player();
        public static int GameTime => Function.Call<int>(Hash.GET_GAME_TIMER);
        public static bool IsControlJustPressed(Control c) => Function.Call<bool>(Hash.IS_CONTROL_JUST_PRESSED, 0, (int)c);
        public static bool IsControlPressed(Control c) => Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 0, (int)c);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        public static bool IsKeyPressed(Keys k) => (GetAsyncKeyState((int)k) & 0x8000) != 0;
    }

    public static class World
    {
        public static Vehicle[] GetAllVehicles()
        {
            int[] buf = new int[256];
            int count = MonoBridgeHost.BridgeGetAllVeh(buf, 256);
            if (count <= 0) return new Vehicle[0];
            Vehicle[] l = new Vehicle[count];
            for (int i = 0; i < count; i++) l[i] = new Vehicle(buf[i]);
            return l;
        }
    }

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

        [DllImport("MiniDotNet.asi", CallingConvention = CallingConvention.Cdecl)] public static extern void BridgeNativeInit(ulong hash);
        [DllImport("MiniDotNet.asi", CallingConvention = CallingConvention.Cdecl)] public static extern void BridgeNativePush64(ulong val);
        [DllImport("MiniDotNet.asi", CallingConvention = CallingConvention.Cdecl)] public static extern ulong BridgeNativeCall();
        [DllImport("MiniDotNet.asi", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr BridgeGetEntityAddr(int handle);
        [DllImport("MiniDotNet.asi", CallingConvention = CallingConvention.Cdecl)] public static extern int BridgeGetAllVeh(int[] arr, int arrSize);

        public static void InitializeAll()
        {
            if (_loaded) return;
            _loaded = true;
            string scriptsDir = Path.Combine(Directory.GetCurrentDirectory(), "scripts");
            if (!Directory.Exists(scriptsDir)) return;

            Log("=== Mono Bridge Starting ===");

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
