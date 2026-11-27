using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MiniDotNet.Core
{
    public class ModItem
    {
        public string Name { get; set; }
        public Action TickAction { get; set; }
        public bool IsEnabled { get; set; } = true;
    }

    public static class MiniMenu
    {
        public static List<ModItem> Mods = new List<ModItem>();
        public static bool IsOpen = false;
        private static int _selectedIndex = 0;
        private static bool _keyPressed = false;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int VK_F2 = 0x71;
        private const int VK_UP = 0x26;
        private const int VK_DOWN = 0x28;
        private const int VK_RETURN = 0x0D;

        private static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

        public static void Tick()
        {
            HandleInput();

            if (IsOpen)
            {
                DrawMenu();
            }

            foreach (var mod in Mods)
            {
                if (mod.IsEnabled && mod.TickAction != null)
                {
                    try
                    {
                        mod.TickAction.Invoke();
                    }
                    catch (Exception ex)
                    {
                        mod.IsEnabled = false;
                        ScriptEngine.Log($"[Mod Error] {mod.Name} disabled: {ex.Message}");
                    }
                }
            }
        }

        private static void HandleInput()
        {
            bool f2 = IsKeyDown(VK_F2);
            bool up = IsKeyDown(VK_UP);
            bool down = IsKeyDown(VK_DOWN);
            bool enter = IsKeyDown(VK_RETURN);

            if (!f2 && !up && !down && !enter)
            {
                _keyPressed = false;
                return;
            }

            if (_keyPressed) return;
            _keyPressed = true;

            if (f2)
            {
                IsOpen = !IsOpen;
                return;
            }

            if (!IsOpen || Mods.Count == 0) return;

            if (up)
                _selectedIndex = (_selectedIndex - 1 + Mods.Count) % Mods.Count;
            else if (down)
                _selectedIndex = (_selectedIndex + 1) % Mods.Count;
            else if (enter)
                Mods[_selectedIndex].IsEnabled = !Mods[_selectedIndex].IsEnabled;
        }

        private static void DrawMenu()
        {
            DrawRect(0.14f, 0.08f, 0.22f, 0.045f, 15, 15, 15, 235);
            DrawText("MiniDotNet [F2]", 0.04f, 0.065f, 0.45f, 255, 200, 0, 255);

            if (Mods.Count == 0)
            {
                DrawRect(0.14f, 0.125f, 0.22f, 0.04f, 0, 0, 0, 190);
                DrawText("No Mods Loaded", 0.04f, 0.112f, 0.35f, 200, 200, 200, 255);
                return;
            }

            for (int i = 0; i < Mods.Count; i++)
            {
                float y = 0.125f + (i * 0.038f);
                bool selected = (i == _selectedIndex);

                if (selected)
                    DrawRect(0.14f, y, 0.22f, 0.036f, 35, 110, 210, 220);
                else
                    DrawRect(0.14f, y, 0.22f, 0.036f, 0, 0, 0, 180);

                DrawText(Mods[i].Name, 0.04f, y - 0.013f, 0.35f, 255, 255, 255, 255);

                if (Mods[i].IsEnabled)
                    DrawText("[ON]", 0.20f, y - 0.013f, 0.35f, 80, 255, 80, 255);
                else
                    DrawText("[OFF]", 0.20f, y - 0.013f, 0.35f, 255, 80, 80, 255);
            }
        }

        private static void DrawRect(float x, float y, float w, float h, int r, int g, int b, int a)
        {
            ScriptEngine.NativeInit(0x3A618A217E5154F0);
            ScriptEngine.PushFloat(x);
            ScriptEngine.PushFloat(y);
            ScriptEngine.PushFloat(w);
            ScriptEngine.PushFloat(h);
            ScriptEngine.PushInt(r);
            ScriptEngine.PushInt(g);
            ScriptEngine.PushInt(b);
            ScriptEngine.PushInt(a);
            ScriptEngine.NativeCall();
        }

        private static void DrawText(string text, float x, float y, float scale, int r, int g, int b, int a)
        {
            ScriptEngine.NativeInit(0x66E0276CC5F6B9DA);
            ScriptEngine.PushInt(0);
            ScriptEngine.NativeCall();

            ScriptEngine.NativeInit(0x07C837F9A01C34C9);
            ScriptEngine.PushFloat(scale);
            ScriptEngine.PushFloat(scale);
            ScriptEngine.NativeCall();

            ScriptEngine.NativeInit(0xBE6B23FFA53FB442);
            ScriptEngine.PushInt(r);
            ScriptEngine.PushInt(g);
            ScriptEngine.PushInt(b);
            ScriptEngine.PushInt(a);
            ScriptEngine.NativeCall();

            ScriptEngine.NativeInit(0x25FBB336DF1804CB);
            ScriptEngine.PushString("STRING", out IntPtr strTypePtr);
            ScriptEngine.NativeCall();
            Marshal.FreeHGlobal(strTypePtr);

            ScriptEngine.NativeInit(0x6C188BE134E074AA);
            ScriptEngine.PushString(text, out IntPtr textPtr);
            ScriptEngine.NativeCall();
            Marshal.FreeHGlobal(textPtr);

            ScriptEngine.NativeInit(0xCD015E5BB0D96A57);
            ScriptEngine.PushFloat(x);
            ScriptEngine.PushFloat(y);
            ScriptEngine.NativeCall();
        }
    }
}
