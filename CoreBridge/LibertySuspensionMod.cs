// Created by VAXIStaa
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;

public class LibertySuspensionMod : Script
{
    const string BuildTag = "VkFYSVNUQUEtSVZIQU5ETElORy1PUklHSU5BTC1CVUlMRA==";

    class ClassToggle
    {
        public VehicleClass ClassType;
        public string Label;
        public bool Enabled;
        public bool DefaultEnabled;

        public ClassToggle(VehicleClass classType, string label, bool defaultEnabled)
        {
            ClassType = classType;
            Label = label;
            Enabled = defaultEnabled;
            DefaultEnabled = defaultEnabled;
        }
    }

    static readonly HashSet<VehicleClass> PermanentlyExcludedClasses = new HashSet<VehicleClass>
    {
        VehicleClass.Boats,
        VehicleClass.Helicopters,
        VehicleClass.Planes,
        VehicleClass.Trains,
    };

    List<ClassToggle> classToggles = new List<ClassToggle>
    {
        new ClassToggle(VehicleClass.Super, "Super Cars", false),
        new ClassToggle(VehicleClass.Motorcycles, "Motorcycles", false),
        new ClassToggle(VehicleClass.Cycles, "Bicycles", false),
    };

    bool IsClassEnabled(VehicleClass cls)
    {
        if (PermanentlyExcludedClasses.Contains(cls))
            return false;
        foreach (ClassToggle t in classToggles)
        {
            if (t.ClassType == cls)
                return t.Enabled;
        }
        return true;
    }

    const int SweepIntervalMs = 1000;

    readonly string SettingsPath;
    readonly string DebugLogPath;

    HandlingParam pSuspensionForce = new HandlingParam("Suspension Force", 0.64f, 0.05f, 1.90f, 0.01f);
    HandlingParam pCompDamp = new HandlingParam("Compression Damping", 0.77f, 0.03f, 2.85f, 0.01f);
    HandlingParam pReboundDamp = new HandlingParam("Rebound Damping (bounce decay)", 0.02f, 0.01f, 3.15f, 0.01f);
    HandlingParam pSuspensionTravel = new HandlingParam("Suspension Travel", 1.91f, 0.10f, 3.70f, 0.02f);
    HandlingParam pRideHeight = new HandlingParam("Ride Height (lowrider <-> lifted)", 0.025f, -0.43f, 0.38f, 0.005f);
    HandlingParam pAntiRollBar = new HandlingParam("Anti-Roll Bar Force (less = more lean)", 0.86f, 0.00f, 2.45f, 0.02f);
    HandlingParam pRollCentreHeight = new HandlingParam("Roll Centre Height (lower = more roll)", 0.83f, 0.05f, 1.75f, 0.02f);
    HandlingParam pMass = new HandlingParam("Mass (heavier = slower accel)", 1.03f, 0.54f, 1.75f, 0.01f);
    HandlingParam pInertia = new HandlingParam("Rotational Inertia (higher = harder to spin)", 1.25f, 0.20f, 4.00f, 0.02f);

    List<HandlingParam> paramList;

    Keys ToggleKey = Keys.F8;
    Keys MenuKey = Keys.F7;

    bool enabled = true;
    bool errorShown = false;
    bool menuOpen = false;
    int selectedIndex = 0;
    int nextSweepMs = 0;

    int lastInputMs = 0;
    float leftHoldTime = 0f;
    float rightHoldTime = 0f;
    float leftRepeatAccum = 0f;
    float rightRepeatAccum = 0f;
    bool settingsDirty = false;
    bool mouseDragging = false;
    int hoverIndex = -1;

    const float RepeatInitialDelay = 0.35f;
    const float RepeatMinRate = 6f;
    const float RepeatMaxRate = 40f;
    const float RepeatAccel = 40f;
    const float CoarseStepMultiplier = 8f;

    class Baseline
    {
        public float SuspensionForce;
        public float CompDamp;
        public float ReboundDamp;
        public float UpperLimit;
        public float LowerLimit;
        public float Raise;
        public float AntiRollBar;
        public float RollCentreFront;
        public float RollCentreRear;
        public float Mass;
        public Vector3 Inertia;
    }

    Dictionary<int, Baseline> baselineByModelHash = new Dictionary<int, Baseline>();
    HashSet<int> loggedModelHashes = new HashSet<int>();
    HashSet<int> settledVehicleHandles = new HashSet<int>();
    HashSet<int> currentSweepHandles = new HashSet<int>();

    Dictionary<Keys, bool> prevKeyState = new Dictionary<Keys, bool>();

    public LibertySuspensionMod()
    {
        SettingsPath = Path.Combine(BaseDirectory, "LibertySuspensionSettings.ini");
        DebugLogPath = Path.Combine(BaseDirectory, "LibertySuspensionDebug.log");

        paramList = new List<HandlingParam>
        {
            pSuspensionForce, pCompDamp, pReboundDamp, pSuspensionTravel,
            pRideHeight, pAntiRollBar, pRollCentreHeight, pMass, pInertia
        };

        Log("==================== Liberty Suspension loaded ====================");
        LoadSettings();
        SaveSettings();

        Tick += OnTick;

        try
        {
            GTA.UI.Notification.PostTicker(
                string.Format(CultureInfo.InvariantCulture,
                    "~o~Liberty Suspension~w~ by ~y~VAXIStaa~w~ loaded ({0} on/off, {1} tuning menu)", ToggleKey, MenuKey),
                false, false);
        }
        catch (Exception ex)
        {
            Log("Notification.PostTicker threw: " + ex);
        }
    }

    void Log(string message)
    {
        try
        {
            File.AppendAllText(DebugLogPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
        }
        catch
        {
        }
    }

    void ReportErrorOnce(Exception ex)
    {
        Log("ERROR: " + ex);
        if (errorShown)
            return;
        errorShown = true;
        try { GTA.UI.Notification.PostTicker("~r~Liberty Suspension error: " + ex.Message, false, false); } catch { }
    }

    bool JustPressed(Keys k)
    {
        bool now = Game.IsKeyPressed(k);
        bool was;
        prevKeyState.TryGetValue(k, out was);
        prevKeyState[k] = now;
        return now && !was;
    }

    bool HandleRepeat(bool isDown, bool justDown, float dt, ref float holdTime, ref float accum)
    {
        bool trigger = false;
        if (justDown)
        {
            trigger = true;
            holdTime = 0f;
            accum = 0f;
        }
        else if (isDown)
        {
            holdTime += dt;
            if (holdTime > RepeatInitialDelay)
            {
                float t = holdTime - RepeatInitialDelay;
                float rate = RepeatMinRate + t * RepeatAccel;
                if (rate > RepeatMaxRate) rate = RepeatMaxRate;
                accum += rate * dt;
                while (accum >= 1f)
                {
                    trigger = true;
                    accum -= 1f;
                }
            }
        }
        else
        {
            holdTime = 0f;
            accum = 0f;
        }
        return trigger;
    }

    void HandleInput()
    {
        int nowMs = Game.GameTime;
        float dt = (lastInputMs == 0) ? 0.0166f : (nowMs - lastInputMs) / 1000.0f;
        lastInputMs = nowMs;
        if (dt < 0f || dt > 0.25f)
            dt = 0.0166f;

        if (JustPressed(ToggleKey))
        {
            enabled = !enabled;
            Log(ToggleKey + " pressed, enabled=" + enabled);
            if (!enabled)
            {
                RestoreAllToVanilla();
            }
            GTA.UI.Screen.ShowSubtitle(
                enabled
                    ? string.Format(CultureInfo.InvariantCulture, "~o~Liberty Suspension: ON ({0})", ToggleKey)
                    : string.Format(CultureInfo.InvariantCulture, "~r~Liberty Suspension: OFF ({0})", ToggleKey),
                2000);
        }

        if (JustPressed(MenuKey))
        {
            menuOpen = !menuOpen;
        }

        if (Game.IsKeyPressed(Keys.ControlKey) && Game.IsKeyPressed(Keys.Menu) && JustPressed(Keys.F9))
        {
            try
            {
                byte[] raw = Convert.FromBase64String(BuildTag);
                string decoded = System.Text.Encoding.ASCII.GetString(raw);
                GTA.UI.Notification.PostTicker("~b~" + decoded, false, false);
            }
            catch (Exception ex)
            {
                Log("Watermark reveal failed: " + ex);
            }
        }

        if (menuOpen)
        {
            int sliderCount = paramList.Count;
            int classCount = classToggles.Count;
            int totalCount = sliderCount + classCount;

            if (JustPressed(Keys.Up) || Game.IsControlJustPressed(GTA.Control.FrontendUp))
                selectedIndex = (selectedIndex - 1 + totalCount) % totalCount;
            if (JustPressed(Keys.Down) || Game.IsControlJustPressed(GTA.Control.FrontendDown))
                selectedIndex = (selectedIndex + 1) % totalCount;

            bool coarse = Game.IsKeyPressed(Keys.ShiftKey)
                || Game.IsControlPressed(GTA.Control.FrontendLb)
                || Game.IsControlPressed(GTA.Control.FrontendRb);

            bool leftDown = Game.IsKeyPressed(Keys.Left) || Game.IsControlPressed(GTA.Control.FrontendLeft);
            bool leftJust = JustPressed(Keys.Left) || Game.IsControlJustPressed(GTA.Control.FrontendLeft);
            bool rightDown = Game.IsKeyPressed(Keys.Right) || Game.IsControlPressed(GTA.Control.FrontendRight);
            bool rightJust = JustPressed(Keys.Right) || Game.IsControlJustPressed(GTA.Control.FrontendRight);

            bool isSlider = selectedIndex < sliderCount;
            bool isClass = !isSlider;

            if (isClass)
            {
                if (leftJust || rightJust || Game.IsControlJustPressed(GTA.Control.FrontendAccept))
                    ToggleClass(selectedIndex - sliderCount);
            }
            else
            {
                if (HandleRepeat(leftDown, leftJust, dt, ref leftHoldTime, ref leftRepeatAccum))
                    AdjustSelected(-1, coarse);
                if (HandleRepeat(rightDown, rightJust, dt, ref rightHoldTime, ref rightRepeatAccum))
                    AdjustSelected(1, coarse);
            }

            if (JustPressed(Keys.Back) || Game.IsControlJustPressed(GTA.Control.FrontendY))
            {
                if (isClass)
                {
                    ClassToggle t = classToggles[selectedIndex - sliderCount];
                    if (t.Enabled != t.DefaultEnabled)
                        ToggleClass(selectedIndex - sliderCount);
                }
                else
                {
                    HandlingParam p = paramList[selectedIndex];
                    p.Value = p.Default;
                    SaveSettings();
                }
            }
            if (JustPressed(Keys.Home) || Game.IsControlJustPressed(GTA.Control.FrontendX))
            {
                foreach (HandlingParam p in paramList) p.Value = p.Default;
                foreach (ClassToggle t in classToggles)
                {
                    if (t.Enabled != t.DefaultEnabled)
                    {
                        t.Enabled = t.DefaultEnabled;
                        if (!t.Enabled)
                            RestoreClassToVanilla(t.ClassType);
                    }
                }
                SaveSettings();
            }

            if (settingsDirty && !leftDown && !rightDown && !mouseDragging)
            {
                SaveSettings();
                settingsDirty = false;
            }
        }
    }

    void AdjustSelected(int dir, bool coarse)
    {
        HandlingParam p = paramList[selectedIndex];
        float step = coarse ? p.Step * CoarseStepMultiplier : p.Step;
        p.Value = Clamp(p.Value + dir * step, p.Min, p.Max);
        settingsDirty = true;
    }

    void ToggleClass(int toggleIndex)
    {
        ClassToggle t = classToggles[toggleIndex];
        t.Enabled = !t.Enabled;
        Log("Class toggle: " + t.ClassType + " -> " + t.Enabled);
        if (!t.Enabled)
            RestoreClassToVanilla(t.ClassType);
        SaveSettings();
    }

    Vehicle GetPlayerVehicleOrNull()
    {
        Ped ped = Game.Player.Character;
        if (ped != null && ped.Exists() && ped.IsInVehicle())
            return ped.CurrentVehicle;
        return null;
    }

    void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;
            foreach (string rawLine in File.ReadAllLines(SettingsPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;
                int eq = line.IndexOf('=');
                if (eq < 0)
                    continue;
                string name = line.Substring(0, eq).Trim();
                string valStr = line.Substring(eq + 1).Trim();

                if (name.StartsWith("Class:", StringComparison.Ordinal))
                {
                    string clsName = name.Substring(6);
                    foreach (ClassToggle t in classToggles)
                    {
                        if (t.ClassType.ToString() == clsName)
                        {
                            t.Enabled = valStr == "1";
                            break;
                        }
                    }
                    continue;
                }

                if (name == "ToggleKey" || name == "MenuKey")
                {
                    Keys parsedKey;
                    if (Enum.TryParse(valStr, true, out parsedKey))
                    {
                        if (name == "ToggleKey") ToggleKey = parsedKey;
                        else MenuKey = parsedKey;
                    }
                    else
                    {
                        Log("LoadSettings: invalid key name for " + name + "='" + valStr + "', keeping default");
                    }
                    continue;
                }

                float val;
                if (!float.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
                    continue;
                foreach (HandlingParam p in paramList)
                {
                    if (p.Name == name)
                    {
                        p.Value = Clamp(val, p.Min, p.Max);
                        break;
                    }
                }
            }
            Log("LoadSettings: read OK from " + SettingsPath);
        }
        catch (Exception ex)
        {
            Log("LoadSettings FAILED: " + ex);
        }
    }

    void SaveSettings()
    {
        try
        {
            string dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            using (StreamWriter w = new StreamWriter(SettingsPath, false))
            {
                w.WriteLine("# build=" + BuildTag);
                w.WriteLine("ToggleKey=" + ToggleKey);
                w.WriteLine("MenuKey=" + MenuKey);
                foreach (HandlingParam p in paramList)
                    w.WriteLine(p.Name + "=" + p.Value.ToString(CultureInfo.InvariantCulture));
                foreach (ClassToggle t in classToggles)
                    w.WriteLine("Class:" + t.ClassType + "=" + (t.Enabled ? "1" : "0"));
            }
            Log("SaveSettings: wrote OK to " + SettingsPath);
        }
        catch (Exception ex)
        {
            Log("SaveSettings FAILED: " + ex);
        }
    }

    void OnTick(object sender, EventArgs e)
    {
        try
        {
            HandleInput();
        }
        catch (Exception ex)
        {
            ReportErrorOnce(ex);
        }

        if (enabled)
        {
            try
            {
                Vehicle playerVehicle = GetPlayerVehicleOrNull();
                if (playerVehicle != null)
                {
                    ApplyToVehicle(playerVehicle);
                }

                int now = Game.GameTime;
                if (now >= nextSweepMs)
                {
                    nextSweepMs = now + SweepIntervalMs;
                    SweepAllVehicles();
                }
            }
            catch (Exception ex)
            {
                ReportErrorOnce(ex);
            }
        }

        if (menuOpen)
        {
            try
            {
                DrawMenu();
            }
            catch (Exception ex)
            {
                ReportErrorOnce(ex);
            }
        }
    }

    void SweepAllVehicles()
    {
        Vehicle[] vehicles = World.GetAllVehicles();
        currentSweepHandles.Clear();
        for (int i = 0; i < vehicles.Length; i++)
        {
            Vehicle v = vehicles[i];
            if (v == null || !v.Exists())
                continue;
            currentSweepHandles.Add(v.Handle);
            ApplyToVehicle(v);
        }

        if (settledVehicleHandles.Count > 0)
            settledVehicleHandles.RemoveWhere(h => !currentSweepHandles.Contains(h));
    }

    Baseline GetOrCacheBaseline(Vehicle v)
    {
        int hash = v.Model.Hash;
        Baseline b;
        if (baselineByModelHash.TryGetValue(hash, out b))
            return b;

        HandlingData h = v.HandlingData;
        b = new Baseline
        {
            SuspensionForce = h.SuspensionForce,
            CompDamp = h.SuspensionCompressionDamping,
            ReboundDamp = h.SuspensionReboundDamping,
            UpperLimit = h.SuspensionUpperLimit,
            LowerLimit = h.SuspensionLowerLimit,
            Raise = h.SuspensionRaise,
            AntiRollBar = h.AntiRollBarForce,
            RollCentreFront = h.RollCenterHeightFront,
            RollCentreRear = h.RollCenterHeightRear,
            Mass = h.Mass,
            Inertia = h.InertiaMultiplier,
        };
        baselineByModelHash[hash] = b;

        if (loggedModelHashes.Add(hash))
        {
            Log(string.Format(CultureInfo.InvariantCulture,
                "New model cached: {0} class={1}", v.DisplayName, v.ClassType));
        }
        return b;
    }

    void ApplyToVehicle(Vehicle v)
    {
        if (v == null || !v.Exists())
            return;

        if (!IsClassEnabled(v.ClassType))
            return;

        Baseline b = GetOrCacheBaseline(v);

        HandlingData h = v.HandlingData;

        h.SuspensionForce = ClampMin(b.SuspensionForce * pSuspensionForce.Value, 0.5f);
        h.SuspensionCompressionDamping = ClampMin(b.CompDamp * pCompDamp.Value, 0.08f);
        h.SuspensionReboundDamping = ClampMin(b.ReboundDamp * pReboundDamp.Value, 0.06f);
        h.SuspensionUpperLimit = ClampMax(b.UpperLimit * pSuspensionTravel.Value, 0.30f);
        h.SuspensionLowerLimit = ClampMin(b.LowerLimit * pSuspensionTravel.Value, -0.30f);
        h.SuspensionRaise = b.Raise + pRideHeight.Value;
        h.AntiRollBarForce = ClampMin(b.AntiRollBar * pAntiRollBar.Value, 0.0f);
        h.RollCenterHeightFront = ClampMin(b.RollCentreFront * pRollCentreHeight.Value, 0.03f);
        h.RollCenterHeightRear = ClampMin(b.RollCentreRear * pRollCentreHeight.Value, 0.03f);
        h.Mass = ClampMin(b.Mass * pMass.Value, 400f);
        h.InertiaMultiplier = ClampVectorMin(b.Inertia * pInertia.Value, 0.05f);

        if (settledVehicleHandles.Add(v.Handle))
        {
            try { v.PlaceOnGround(); } catch (Exception ex) { Log("PlaceOnGround (apply) failed: " + ex); }
        }
    }

    void RestoreVehicleToBaseline(Vehicle v, Baseline b)
    {
        HandlingData h = v.HandlingData;
        h.SuspensionForce = b.SuspensionForce;
        h.SuspensionCompressionDamping = b.CompDamp;
        h.SuspensionReboundDamping = b.ReboundDamp;
        h.SuspensionUpperLimit = b.UpperLimit;
        h.SuspensionLowerLimit = b.LowerLimit;
        h.SuspensionRaise = b.Raise;
        h.AntiRollBarForce = b.AntiRollBar;
        h.RollCenterHeightFront = b.RollCentreFront;
        h.RollCenterHeightRear = b.RollCentreRear;
        h.Mass = b.Mass;
        h.InertiaMultiplier = b.Inertia;

        try { v.PlaceOnGround(); } catch (Exception ex) { Log("PlaceOnGround (restore) failed: " + ex); }
        settledVehicleHandles.Remove(v.Handle);
    }

    void RestoreAllToVanilla()
    {
        try
        {
            Vehicle[] vehicles = World.GetAllVehicles();
            for (int i = 0; i < vehicles.Length; i++)
            {
                Vehicle v = vehicles[i];
                if (v == null || !v.Exists())
                    continue;
                Baseline b;
                if (!baselineByModelHash.TryGetValue(v.Model.Hash, out b))
                    continue;
                RestoreVehicleToBaseline(v, b);
            }
        }
        catch (Exception ex)
        {
            ReportErrorOnce(ex);
        }
    }

    void RestoreClassToVanilla(VehicleClass cls)
    {
        try
        {
            Vehicle[] vehicles = World.GetAllVehicles();
            for (int i = 0; i < vehicles.Length; i++)
            {
                Vehicle v = vehicles[i];
                if (v == null || !v.Exists() || v.ClassType != cls)
                    continue;
                Baseline b;
                if (!baselineByModelHash.TryGetValue(v.Model.Hash, out b))
                    continue;
                RestoreVehicleToBaseline(v, b);
            }
        }
        catch (Exception ex)
        {
            ReportErrorOnce(ex);
        }
    }

    static float ClampMin(float v, float min) { return v < min ? min : v; }
    static float ClampMax(float v, float max) { return v > max ? max : v; }
    static float Clamp(float v, float min, float max) { return v < min ? min : (v > max ? max : v); }
    static Vector3 ClampVectorMin(Vector3 v, float min)
    {
        return new Vector3(ClampMin(v.X, min), ClampMin(v.Y, min), ClampMin(v.Z, min));
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    static bool TryGetNormalizedMouse(out float nx, out float ny)
    {
        nx = -1f;
        ny = -1f;
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;
        RECT rect;
        if (!GetClientRect(hwnd, out rect))
            return false;
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0)
            return false;
        System.Drawing.Point screenPos = Cursor.Position;
        POINT pt = new POINT { X = screenPos.X, Y = screenPos.Y };
        if (!ScreenToClient(hwnd, ref pt))
            return false;
        nx = Clamp((float)pt.X / w, 0f, 1f);
        ny = Clamp((float)pt.Y / h, 0f, 1f);
        return true;
    }

    static void DrawRect(float centerX, float centerY, float width, float height, int r, int g, int b, int a)
    {
        Function.Call(Hash.DRAW_RECT, centerX, centerY, width, height, r, g, b, a, false);
    }

    static void DrawText(string text, float x, float y, float scale, int r, int g, int b, int a, int font = 0)
    {
        Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
        Function.Call(Hash.SET_TEXT_FONT, font);
        Function.Call(Hash.SET_TEXT_SCALE, 1.0f, scale);
        Function.Call(Hash.SET_TEXT_COLOUR, r, g, b, a);
        Function.Call(Hash.SET_TEXT_CENTRE, false);
        Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
        Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, x, y, 0);
    }

    static void DrawOutlinedText(string text, float x, float y, float scale, int font,
        int fr, int fg, int fb, int fa, int outR, int outG, int outB, int outA, float outlineOffset)
    {
        DrawText(text, x - outlineOffset, y, scale, outR, outG, outB, outA, font);
        DrawText(text, x + outlineOffset, y, scale, outR, outG, outB, outA, font);
        DrawText(text, x, y - outlineOffset, scale, outR, outG, outB, outA, font);
        DrawText(text, x, y + outlineOffset, scale, outR, outG, outB, outA, font);
        DrawText(text, x - outlineOffset, y - outlineOffset, scale, outR, outG, outB, outA, font);
        DrawText(text, x + outlineOffset, y - outlineOffset, scale, outR, outG, outB, outA, font);
        DrawText(text, x - outlineOffset, y + outlineOffset, scale, outR, outG, outB, outA, font);
        DrawText(text, x + outlineOffset, y + outlineOffset, scale, outR, outG, outB, outA, font);
        DrawText(text, x, y, scale, fr, fg, fb, fa, font);
    }

    const float MenuLeft = 0.02f;
    const float MenuTop = 0.03f;
    const float MenuWidth = 0.36f;
    const float MenuHeaderHeight = 0.118f;
    const float MenuRowHeight = 0.032f;
    const float MenuSliderWidth = 0.11f;
    const float ToggleRowHeight = 0.028f;
    const float SectionLabelHeight = 0.030f;
    const float TogglePillWidth = 0.06f;

    static readonly int[] ColorGlow = { 255, 20, 147 };
    static readonly int[] ColorPanelBg = { 22, 8, 17 };
    static readonly int[] ColorHeaderBase = { 150, 18, 96 };
    static readonly int[] ColorHeaderSheen = { 255, 110, 185 };
    static readonly int[] ColorAccent = { 236, 64, 158 };
    static readonly int[] ColorAccentBright = { 255, 170, 215 };
    static readonly int[] ColorTrack = { 58, 28, 46 };
    static readonly int[] ColorTextNormal = { 240, 225, 235 };
    static readonly int[] ColorTextSelected = { 30, 8, 20 };
    static readonly int[] ColorTextMuted = { 205, 165, 190 };
    static readonly int[] ColorToggleOn = { 236, 64, 158 };
    static readonly int[] ColorToggleOff = { 55, 50, 53 };
    static readonly int[] ColorToggleOnText = { 255, 255, 255 };
    static readonly int[] ColorToggleOffText = { 165, 155, 160 };

    void DrawToggleRow(int i, float rowTop, float mx, float my, bool mouseJustDown,
        string label, bool enabled, int toggleArg, Action<int> onToggle)
    {
        float rowCenterY = rowTop + ToggleRowHeight / 2f;
        float pillLeft = MenuLeft + MenuWidth - TogglePillWidth - 0.05f;
        float labelHighlightLeft = MenuLeft + 0.003f;
        float labelHighlightWidth = (pillLeft - 0.010f) - labelHighlightLeft;
        float labelHighlightCenterX = labelHighlightLeft + labelHighlightWidth / 2f;

        bool overRow = mx >= MenuLeft && mx <= MenuLeft + MenuWidth && my >= rowTop && my <= rowTop + ToggleRowHeight;
        if (overRow)
            hoverIndex = i;
        if (overRow && mouseJustDown)
        {
            selectedIndex = i;
            onToggle(toggleArg);
        }

        bool sel = (i == selectedIndex);
        bool hover = (i == hoverIndex) && !sel;

        if (hover)
            DrawRect(labelHighlightCenterX, rowCenterY, labelHighlightWidth, ToggleRowHeight - 0.002f,
                ColorAccent[0], ColorAccent[1], ColorAccent[2], 60);
        if (sel)
        {
            DrawRect(labelHighlightCenterX, rowCenterY, labelHighlightWidth, ToggleRowHeight - 0.002f,
                ColorAccent[0], ColorAccent[1], ColorAccent[2], 130);
            DrawRect(MenuLeft + 0.004f, rowCenterY, 0.004f, ToggleRowHeight - 0.002f,
                ColorAccentBright[0], ColorAccentBright[1], ColorAccentBright[2], 255);
        }

        int[] labelColor = sel ? ColorTextSelected : ColorTextNormal;
        DrawText(label, MenuLeft + 0.014f, rowTop + 0.004f, 0.19f, labelColor[0], labelColor[1], labelColor[2], 255, 4);

        int[] pillColor = enabled ? ColorToggleOn : ColorToggleOff;
        int[] pillTextColor = enabled ? ColorToggleOnText : ColorToggleOffText;
        DrawRect(pillLeft + TogglePillWidth / 2f, rowCenterY, TogglePillWidth, ToggleRowHeight - 0.008f,
            pillColor[0], pillColor[1], pillColor[2], 255);
        DrawText(enabled ? "ON" : "OFF", pillLeft + (enabled ? 0.017f : 0.013f), rowTop + 0.003f, 0.16f,
            pillTextColor[0], pillTextColor[1], pillTextColor[2], 255, 4);
    }

    void DrawMenu()
    {
        Function.Call(Hash.SET_MOUSE_CURSOR_THIS_FRAME);
        Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.Attack, true);
        Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.Attack2, true);
        Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.Aim, true);
        Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.LookLeftRight, true);
        Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.LookUpDown, true);

        float mx, my;
        if (!TryGetNormalizedMouse(out mx, out my))
        {
            mx = -1f;
            my = -1f;
        }
        bool mouseDown = Game.IsControlPressed(GTA.Control.CursorAccept);
        bool mouseJustDown = Game.IsControlJustPressed(GTA.Control.CursorAccept);
        if (!mouseDown)
            mouseDragging = false;
        hoverIndex = -1;

        int sliderCount = paramList.Count;
        int classCount = classToggles.Count;
        float panelHeight = MenuHeaderHeight + sliderCount * MenuRowHeight
            + SectionLabelHeight + classCount * ToggleRowHeight + 0.012f;
        float panelCenterX = MenuLeft + MenuWidth / 2f;
        float classSectionTop = MenuTop + MenuHeaderHeight + sliderCount * MenuRowHeight + SectionLabelHeight;

        DrawRect(panelCenterX, MenuTop + panelHeight / 2f, MenuWidth + 0.008f, panelHeight + 0.008f,
            ColorGlow[0], ColorGlow[1], ColorGlow[2], 70);
        DrawRect(panelCenterX, MenuTop + panelHeight / 2f, MenuWidth, panelHeight,
            ColorPanelBg[0], ColorPanelBg[1], ColorPanelBg[2], 235);

        DrawRect(panelCenterX, MenuTop + MenuHeaderHeight / 2f, MenuWidth, MenuHeaderHeight,
            ColorHeaderBase[0], ColorHeaderBase[1], ColorHeaderBase[2], 245);
        DrawRect(panelCenterX, MenuTop + MenuHeaderHeight * 0.20f, MenuWidth, MenuHeaderHeight * 0.36f,
            ColorHeaderSheen[0], ColorHeaderSheen[1], ColorHeaderSheen[2], 55);
        DrawRect(panelCenterX, MenuTop + MenuHeaderHeight, MenuWidth, 0.0015f,
            ColorAccentBright[0], ColorAccentBright[1], ColorAccentBright[2], 200);

        DrawOutlinedText("Liberty Suspension", MenuLeft + 0.014f, MenuTop + 0.004f, 0.52f, 1,
            0, 0, 0, 255, ColorGlow[0], ColorGlow[1], ColorGlow[2], 255, 0.0014f);
        DrawText("TUNING  -  by VAXIStaa", MenuLeft + 0.014f, MenuTop + 0.058f, 0.23f,
            ColorTextMuted[0], ColorTextMuted[1], ColorTextMuted[2], 255, 4);
        DrawText("Pad/Arrows: navigate+adjust (hold=fast, LB/RB/Shift=coarse)",
            MenuLeft + 0.014f, MenuTop + 0.082f, 0.19f, ColorTextMuted[0], ColorTextMuted[1], ColorTextMuted[2], 255, 4);
        DrawText("Mouse: click+drag  |  Y/Back reset  |  X/Home reset all",
            MenuLeft + 0.014f, MenuTop + 0.102f, 0.19f, ColorTextMuted[0], ColorTextMuted[1], ColorTextMuted[2], 255, 4);

        for (int i = 0; i < sliderCount; i++)
        {
            HandlingParam p = paramList[i];
            float rowTop = MenuTop + MenuHeaderHeight + i * MenuRowHeight;
            float rowCenterY = rowTop + MenuRowHeight / 2f;
            float sliderLeft = MenuLeft + MenuWidth - MenuSliderWidth - 0.05f;

            float labelHighlightLeft = MenuLeft + 0.003f;
            float labelHighlightWidth = (sliderLeft - 0.010f) - labelHighlightLeft;
            float labelHighlightCenterX = labelHighlightLeft + labelHighlightWidth / 2f;

            bool overRow = mx >= MenuLeft && mx <= MenuLeft + MenuWidth && my >= rowTop && my <= rowTop + MenuRowHeight;
            bool overSlider = overRow && mx >= sliderLeft && mx <= sliderLeft + MenuSliderWidth;
            if (overRow)
                hoverIndex = i;
            if (overRow && mouseJustDown)
                selectedIndex = i;
            if (overSlider && mouseDown)
                mouseDragging = true;

            if (mouseDragging && selectedIndex == i && mouseDown)
            {
                float dragFrac = Clamp((mx - sliderLeft) / MenuSliderWidth, 0f, 1f);
                p.Value = Clamp(p.Min + dragFrac * (p.Max - p.Min), p.Min, p.Max);
                settingsDirty = true;
            }

            bool sel = (i == selectedIndex);
            bool hover = (i == hoverIndex) && !sel;

            if (hover)
                DrawRect(labelHighlightCenterX, rowCenterY, labelHighlightWidth, MenuRowHeight - 0.002f,
                    ColorAccent[0], ColorAccent[1], ColorAccent[2], 60);
            if (sel)
            {
                DrawRect(labelHighlightCenterX, rowCenterY, labelHighlightWidth, MenuRowHeight - 0.002f,
                    ColorAccent[0], ColorAccent[1], ColorAccent[2], 190);
                DrawRect(MenuLeft + 0.004f, rowCenterY, 0.004f, MenuRowHeight - 0.002f,
                    ColorAccentBright[0], ColorAccentBright[1], ColorAccentBright[2], 255);
            }

            int[] labelColor = sel ? ColorTextSelected : ColorTextNormal;
            DrawText(p.Name, MenuLeft + 0.014f, rowTop + 0.005f, 0.225f, labelColor[0], labelColor[1], labelColor[2], 255, 4);

            float frac = (p.Max > p.Min) ? Clamp((p.Value - p.Min) / (p.Max - p.Min), 0f, 1f) : 0f;

            DrawRect(sliderLeft + MenuSliderWidth / 2f, rowCenterY, MenuSliderWidth, 0.007f,
                ColorTrack[0], ColorTrack[1], ColorTrack[2], 230);
            if (frac > 0.003f)
            {
                DrawRect(sliderLeft + (MenuSliderWidth * frac) / 2f, rowCenterY, MenuSliderWidth * frac, 0.010f,
                    ColorAccent[0], ColorAccent[1], ColorAccent[2], 255);
            }
            DrawRect(sliderLeft + MenuSliderWidth * frac, rowCenterY, 0.0035f, 0.017f,
                ColorAccentBright[0], ColorAccentBright[1], ColorAccentBright[2], 255);

            DrawText(p.Value.ToString("0.###"), MenuLeft + MenuWidth - 0.045f, rowTop + 0.005f, 0.20f,
                ColorTextNormal[0], ColorTextNormal[1], ColorTextNormal[2], 255, 4);
        }

        DrawRect(panelCenterX, classSectionTop - SectionLabelHeight / 2f, MenuWidth - 0.02f, 0.0012f,
            ColorAccentBright[0], ColorAccentBright[1], ColorAccentBright[2], 140);
        DrawText("VEHICLE CLASSES  (off by default)", MenuLeft + 0.014f, classSectionTop - SectionLabelHeight + 0.005f,
            0.185f, ColorTextMuted[0], ColorTextMuted[1], ColorTextMuted[2], 255, 4);

        for (int ti = 0; ti < classCount; ti++)
        {
            int i = sliderCount + ti;
            ClassToggle t = classToggles[ti];
            float rowTop = classSectionTop + ti * ToggleRowHeight;
            DrawToggleRow(i, rowTop, mx, my, mouseJustDown, t.Label, t.Enabled, ti, ToggleClass);
        }

        DrawText(
            string.Format(CultureInfo.InvariantCulture, "Tracked models: {0}", baselineByModelHash.Count),
            MenuLeft + 0.012f, MenuTop + panelHeight + 0.006f, 0.22f,
            ColorTextMuted[0], ColorTextMuted[1], ColorTextMuted[2], 255, 4);
    }
}

class HandlingParam
{
    public string Name;
    public float Value;
    public float Min;
    public float Max;
    public float Step;
    public float Default;

    public HandlingParam(string name, float defaultValue, float min, float max, float step)
    {
        Name = name;
        Value = defaultValue;
        Default = defaultValue;
        Min = min;
        Max = max;
        Step = step;
    }
}
