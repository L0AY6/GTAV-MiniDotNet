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
