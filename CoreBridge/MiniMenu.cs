using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;

namespace MiniDotNet.Core
{
    public class ModItem
    {
        public string Name { get; set; }
        public Action TickAction { get; set; }
        public Action OpenMenuAction { get; set; }
        public int TriggerKey { get; set; } = 0;
        public bool IsEnabled { get; set; } = true;
    }

    public static unsafe class MiniMenu
    {
        public static List<ModItem> Mods = new List<ModItem>();
        public static bool IsOpen = false;
        private static int _selectedIndex = 0;
        private static bool _keyPressed = false;
        private static bool _padComboPressed = false;
        private static bool _padNavPressed = false;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const int VK_F2 = 0x71;
        private const int VK_UP = 0x26;
        private const int VK_DOWN = 0x28;
        private const int VK_RIGHT = 0x27;
        private const int VK_RETURN = 0x0D;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const int INPUT_COVER = 44;
        private const int INPUT_FRONTEND_DOWN = 187;
        private const int INPUT_FRONTEND_UP = 188;
        private const int INPUT_FRONTEND_LEFT = 189;
        private const int INPUT_FRONTEND_RIGHT = 190;
        private const int INPUT_FRONTEND_ACCEPT = 201;

        private static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

        private static bool IsControlPressed(int control)
        {
            ScriptEngine.NativeInit(0xE2587F8CBBD87B1D);
            ScriptEngine.PushInt(0);
            ScriptEngine.PushInt(control);
            ScriptEngine.NativeCall();
            return ScriptEngine.GetResultInt() != 0;
        }

        public static void SimulateKeyPress(int vKey)
        {
            if (vKey <= 0 || vKey > 255) return;
            byte vk = (byte)vKey;
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void Tick()
        {
            HandleKeyboardInput();
            HandleGamepadInput();

            if (IsOpen)
            {
                DrawMenyooStyle();
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

        private static void TriggerSelectedModMenu()
        {
            if (_selectedIndex < 0 || _selectedIndex >= Mods.Count) return;
            var mod = Mods[_selectedIndex];

            if (mod.OpenMenuAction != null)
            {
                IsOpen = false;
                mod.OpenMenuAction.Invoke();
            }
            else if (mod.TriggerKey > 0)
            {
                IsOpen = false;
                SimulateKeyPress(mod.TriggerKey);
            }
        }

        private static void HandleKeyboardInput()
        {
            bool f2 = IsKeyDown(VK_F2);
            bool up = IsKeyDown(VK_UP);
            bool down = IsKeyDown(VK_DOWN);
            bool right = IsKeyDown(VK_RIGHT);
            bool enter = IsKeyDown(VK_RETURN);

            if (!f2 && !up && !down && !right && !enter)
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
            else if (right)
                TriggerSelectedModMenu();
            else if (enter)
                Mods[_selectedIndex].IsEnabled = !Mods[_selectedIndex].IsEnabled;
        }
        private static void HandleGamepadInput()
        {
            bool combo = IsControlPressed(INPUT_FRONTEND_LEFT) &&
                         IsControlPressed(INPUT_COVER) &&
                         IsControlPressed(INPUT_FRONTEND_ACCEPT);

            if (combo)
            {
                if (!_padComboPressed)
                {
                    IsOpen = !IsOpen;
                    _padComboPressed = true;
                    _padNavPressed = true;
                }
                return;
            }
            else
            {
                _padComboPressed = false;
            }

            if (!IsOpen || Mods.Count == 0) return;

            bool up = IsControlPressed(INPUT_FRONTEND_UP);
            bool down = IsControlPressed(INPUT_FRONTEND_DOWN);
            bool right = IsControlPressed(INPUT_FRONTEND_RIGHT);
            bool accept = IsControlPressed(INPUT_FRONTEND_ACCEPT);

            if (!up && !down && !right && !accept)
            {
                _padNavPressed = false;
                return;
            }

            if (_padNavPressed) return;
            _padNavPressed = true;

            if (up)
                _selectedIndex = (_selectedIndex - 1 + Mods.Count) % Mods.Count;
            else if (down)
                _selectedIndex = (_selectedIndex + 1) % Mods.Count;
            else if (right)
                TriggerSelectedModMenu();
            else if (accept)
                Mods[_selectedIndex].IsEnabled = !Mods[_selectedIndex].IsEnabled;
        }

        private static void DrawMenyooStyle()
        {
            float menuX = 0.145f;
            float menuW = 0.225f;
            float leftTextX = menuX - (menuW / 2f) + 0.008f;
            float rightTextX = menuX + (menuW / 2f) - 0.042f;

            float bannerY = 0.14f;
            float bannerH = 0.082f;
            DrawRect(menuX, bannerY, menuW, bannerH, 92, 45, 165, 240);
            DrawText("by L0AY6", menuX - 0.025f, bannerY - 0.032f, 0.28f, 0, 220, 200, 255, 255);
            DrawText("MiniDotNet", menuX - 0.055f, bannerY - 0.015f, 0.85f, 1, 255, 255, 255, 255);

            float subY = bannerY + (bannerH / 2f) + 0.015f;
            float subH = 0.030f;
            DrawRect(menuX, subY, menuW, subH, 10, 10, 10, 245);
            DrawText("INSTALLED MODS", leftTextX, subY - 0.011f, 0.30f, 0, 180, 140, 255, 255);

            string counter = Mods.Count > 0 ? $"{_selectedIndex + 1} / {Mods.Count}" : "0 / 0";
            DrawText(counter, rightTextX - 0.005f, subY - 0.011f, 0.30f, 0, 200, 200, 200, 255);

            float startY = subY + (subH / 2f) + 0.017f;
            float itemH = 0.034f;

            if (Mods.Count == 0)
            {
                DrawRect(menuX, startY, menuW, itemH, 15, 15, 15, 200);
                DrawText("No Mods Loaded", leftTextX, startY - 0.012f, 0.34f, 0, 180, 180, 180, 255);
                return;
            }

            for (int i = 0; i < Mods.Count; i++)
            {
                float y = startY + (i * itemH);
                bool selected = (i == _selectedIndex);
                bool hasMenu = (Mods[i].OpenMenuAction != null || Mods[i].TriggerKey > 0);
                string suffix = hasMenu ? " >>" : "";

                if (selected)
                {
                    DrawRect(menuX, y, menuW, itemH, 245, 245, 245, 240);
                    DrawText(Mods[i].Name, leftTextX, y - 0.012f, 0.34f, 0, 10, 10, 10, 255);

                    if (Mods[i].IsEnabled)
                        DrawText("ON" + suffix, rightTextX, y - 0.012f, 0.34f, 0, 0, 160, 0, 255);
                    else
                        DrawText("OFF" + suffix, rightTextX, y - 0.012f, 0.34f, 0, 180, 0, 0, 255);
                }
                else
                {
                    DrawRect(menuX, y, menuW, itemH, 15, 15, 15, 205);
                    DrawText(Mods[i].Name, leftTextX, y - 0.012f, 0.34f, 0, 240, 240, 240, 255);

                    if (Mods[i].IsEnabled)
                        DrawText("ON" + suffix, rightTextX, y - 0.012f, 0.34f, 0, 80, 220, 80, 255);
                    else
                        DrawText("OFF" + suffix, rightTextX, y - 0.012f, 0.34f, 0, 220, 80, 80, 255);
                }
            }

            float footerY = startY + (Mods.Count * itemH) - (itemH / 2f) + 0.014f;
            DrawRect(menuX, footerY, menuW, 0.028f, 10, 10, 10, 245);
            DrawText("ENTER: Toggle | RIGHT: Open Menu", leftTextX, footerY - 0.010f, 0.26f, 0, 150, 150, 150, 255);
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

        private static void DrawText(string text, float x, float y, float scale, int font, int r, int g, int b, int a)
        {
            ScriptEngine.NativeInit(0x66E0276CC5F6B9DA);
            ScriptEngine.PushInt(font);
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

            byte[] typeBytes = Encoding.ASCII.GetBytes("STRING\0");
            byte[] textBytes = Encoding.UTF8.GetBytes(text + "\0");

            fixed (byte* pType = typeBytes)
            fixed (byte* pText = textBytes)
            {
                ScriptEngine.NativeInit(0x25FBB336DF1804CB);
                ScriptEngine.PushPtr((IntPtr)pType);
                ScriptEngine.NativeCall();

                ScriptEngine.NativeInit(0x6C188BE134E074AA);
                ScriptEngine.PushPtr((IntPtr)pText);
                ScriptEngine.NativeCall();

                ScriptEngine.NativeInit(0xCD015E5BB0D96A57);
                ScriptEngine.PushFloat(x);
                ScriptEngine.PushFloat(y);
                ScriptEngine.NativeCall();
            }
        }
    }
}
