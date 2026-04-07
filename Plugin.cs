using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using InputFramework;
using Rewired;
using UnityEngine;

[BepInPlugin("com.noms.splitthrottle", "SplitThrottle", "1.2.0")]
[BepInDependency("experimental.assassin1076.extrainputframework", BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BaseUnityPlugin
{
    // === Rewired Action Names ===
    internal const string ACT_LEFT_UP = "SplitThrottle::LeftUp";
    internal const string ACT_LEFT_DOWN = "SplitThrottle::LeftDown";
    internal const string ACT_RIGHT_UP = "SplitThrottle::RightUp";
    internal const string ACT_RIGHT_DOWN = "SplitThrottle::RightDown";
    internal const string ACT_SYNC = "SplitThrottle::Sync";
    internal const string ACT_LEFT_AXIS = "SplitThrottle::LeftAxis";
    internal const string ACT_RIGHT_AXIS = "SplitThrottle::RightAxis";

    // === Shared State ===
    internal static bool splitActive;
    internal static float leftThrottle;
    internal static float rightThrottle;
    internal static ManualLogSource Log;

    // === Config ===
    internal static ConfigEntry<bool> cfgEnabled;
    internal static ConfigEntry<float> cfgKeyboardStep;
    internal static ConfigEntry<bool> cfgShowOverlay;
    internal static ConfigEntry<int> cfgLeftAxisIndex;
    internal static ConfigEntry<int> cfgRightAxisIndex;
    internal static ConfigEntry<bool> cfgInvertLeft;
    internal static ConfigEntry<bool> cfgInvertRight;
    internal static ConfigEntry<string> cfgControllerName;
    internal static ConfigEntry<string> cfgDisabledAircraft;

    internal static HashSet<string> disabledNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Engine side cache
    internal static readonly Dictionary<Turbojet, EngineSide> turbojetSideCache = new Dictionary<Turbojet, EngineSide>();
    internal static readonly Dictionary<DuctedFan, EngineSide> ductedFanSideCache = new Dictionary<DuctedFan, EngineSide>();

    internal enum EngineSide { Left, Right, Center }

    void Awake()
    {
        Log = Logger;

        cfgEnabled = Config.Bind("General", "Enabled", true, "Enable split throttle mod");
        cfgKeyboardStep = Config.Bind("General", "ThrottleStep", 0.02f,
            "Throttle change per frame when holding keyboard key");
        cfgShowOverlay = Config.Bind("General", "ShowOverlay", false, "Show throttle overlay on screen");
        cfgDisabledAircraft = Config.Bind("General", "DisabledAircraft", "Chicane,SAH,AttackHelo,Tarantula,Ibis",
            "Comma-separated aircraft names where split throttle is disabled (partial match, case-insensitive)");

        cfgLeftAxisIndex = Config.Bind("HOTAS", "LeftAxisIndex", 1,
            "Rewired joystick axis index for left throttle lever. 0-based.");
        cfgRightAxisIndex = Config.Bind("HOTAS", "RightAxisIndex", 0,
            "Rewired joystick axis index for right throttle lever. 0-based.");
        cfgInvertLeft = Config.Bind("HOTAS", "InvertLeft", false, "Invert left axis");
        cfgInvertRight = Config.Bind("HOTAS", "InvertRight", false, "Invert right axis");
        cfgControllerName = Config.Bind("HOTAS", "ControllerName", "THROTTLE",
            "Partial name match for throttle controller (case-insensitive)");

        ParseBlacklist();
        cfgDisabledAircraft.SettingChanged += (_, __) => ParseBlacklist();

        // Register Rewired actions via Extra Input Framework
        ExtraInputManager.RegisterAction(ACT_LEFT_UP, InputActionType.Button);
        ExtraInputManager.RegisterAction(ACT_LEFT_DOWN, InputActionType.Button);
        ExtraInputManager.RegisterAction(ACT_RIGHT_UP, InputActionType.Button);
        ExtraInputManager.RegisterAction(ACT_RIGHT_DOWN, InputActionType.Button);
        ExtraInputManager.RegisterAction(ACT_SYNC, InputActionType.Button);
        ExtraInputManager.RegisterAction(ACT_LEFT_AXIS, InputActionType.Axis);
        ExtraInputManager.RegisterAction(ACT_RIGHT_AXIS, InputActionType.Axis);

        try
        {
            var harmony = new Harmony("com.noms.splitthrottle");
            harmony.PatchAll();
            Logger.LogInfo($"Harmony patched {harmony.GetPatchedMethods().Count()} methods.");
        }
        catch (Exception e)
        {
            Logger.LogError($"Harmony patch FAILED: {e}");
        }

        UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) =>
        {
            if (SplitThrottleRunner.Instance != null) return;
            var go = new GameObject("SplitThrottle_Runtime");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<SplitThrottleRunner>();
        };

        Logger.LogInfo("SplitThrottle loaded.");
    }

    static void ParseBlacklist()
    {
        disabledNames.Clear();
        foreach (var name in cfgDisabledAircraft.Value.Split(','))
        {
            var trimmed = name.Trim();
            if (trimmed.Length > 0) disabledNames.Add(trimmed);
        }
    }

    internal static bool IsDisabledAircraft(Aircraft aircraft)
    {
        if (aircraft == null) return true;
        string acName = ((Component)aircraft).name;
        foreach (var disabled in disabledNames)
        {
            if (acName.IndexOf(disabled, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    internal static Aircraft GetLocalAircraft()
    {
        try
        {
            if (GameManager.GetLocalAircraft(out var ac)) return ac;
            return null;
        }
        catch { return null; }
    }

    // === Harmony Patches ===

    // Append mod marker to Application.version so modded clients only match with each other.
    // This prevents the mod from giving an unfair advantage in lobbies where the host doesn't have it.
    [HarmonyPatch(typeof(Application), "get_version")]
    static class ApplicationVersionPatch
    {
        const string MOD_MARKER = "+SplitThrottle";

        static void Postfix(ref string __result)
        {
            if (__result != null && !__result.Contains(MOD_MARKER))
                __result = __result + MOD_MARKER;
        }
    }

    [HarmonyPatch(typeof(Turbojet), "FixedUpdate")]
    static class TurbojetPatch
    {
        static readonly FieldInfo f_controlInputs = AccessTools.Field(typeof(Turbojet), "controlInputs");
        static readonly FieldInfo f_aircraft = AccessTools.Field(typeof(Turbojet), "aircraft");
        [ThreadStatic] static float savedThrottle;

        static void Prefix(Turbojet __instance)
        {
            if (!splitActive) return;
            var aircraft = f_aircraft.GetValue(__instance) as Aircraft;
            if (aircraft == null) return;
            var localAircraft = GetLocalAircraft();
            if (localAircraft == null || aircraft != localAircraft) return;
            var inputs = f_controlInputs.GetValue(__instance) as ControlInputs;
            if (inputs == null) return;

            savedThrottle = inputs.throttle;
            if (!turbojetSideCache.TryGetValue(__instance, out var side))
            {
                Vector3 localPos = ((Component)aircraft).transform.InverseTransformPoint(__instance.transform.position);
                side = Mathf.Abs(localPos.x) < 0.5f ? EngineSide.Center
                     : localPos.x < 0 ? EngineSide.Left : EngineSide.Right;
                turbojetSideCache[__instance] = side;
            }
            inputs.throttle = side switch
            {
                EngineSide.Left => leftThrottle,
                EngineSide.Right => rightThrottle,
                _ => (leftThrottle + rightThrottle) * 0.5f
            };
        }

        static void Postfix(Turbojet __instance)
        {
            if (!splitActive) return;
            var aircraft = f_aircraft.GetValue(__instance) as Aircraft;
            if (aircraft == null) return;
            var localAircraft = GetLocalAircraft();
            if (localAircraft == null || aircraft != localAircraft) return;
            var inputs = f_controlInputs.GetValue(__instance) as ControlInputs;
            if (inputs == null) return;
            inputs.throttle = (leftThrottle + rightThrottle) * 0.5f;
        }
    }

    [HarmonyPatch(typeof(DuctedFan), "FixedUpdate")]
    static class DuctedFanPatch
    {
        static readonly FieldInfo f_controlInputs = AccessTools.Field(typeof(DuctedFan), "controlInputs");
        static readonly FieldInfo f_aircraft = AccessTools.Field(typeof(DuctedFan), "aircraft");
        [ThreadStatic] static float savedThrottle;

        static void Prefix(DuctedFan __instance)
        {
            if (!splitActive) return;
            var aircraft = f_aircraft.GetValue(__instance) as Aircraft;
            if (aircraft == null) return;
            var localAircraft = GetLocalAircraft();
            if (localAircraft == null || aircraft != localAircraft) return;
            var inputs = f_controlInputs.GetValue(__instance) as ControlInputs;
            if (inputs == null) return;

            savedThrottle = inputs.throttle;
            if (!ductedFanSideCache.TryGetValue(__instance, out var side))
            {
                Vector3 localPos = ((Component)aircraft).transform.InverseTransformPoint(((Component)__instance).transform.position);
                side = Mathf.Abs(localPos.x) < 0.5f ? EngineSide.Center
                     : localPos.x < 0 ? EngineSide.Left : EngineSide.Right;
                ductedFanSideCache[__instance] = side;
            }
            if (side != EngineSide.Center)
                inputs.throttle = side == EngineSide.Left ? leftThrottle : rightThrottle;
        }

        static void Postfix(DuctedFan __instance)
        {
            if (!splitActive) return;
            var aircraft = f_aircraft.GetValue(__instance) as Aircraft;
            if (aircraft == null) return;
            var localAircraft = GetLocalAircraft();
            if (localAircraft == null || aircraft != localAircraft) return;
            var inputs = f_controlInputs.GetValue(__instance) as ControlInputs;
            if (inputs == null) return;
            inputs.throttle = (leftThrottle + rightThrottle) * 0.5f;
        }
    }

    [HarmonyPatch(typeof(ConstantSpeedProp), "FixedUpdate")]
    static class ConstantSpeedPropPatch
    {
        static readonly FieldInfo f_controlInputs = AccessTools.Field(typeof(ConstantSpeedProp), "controlInputs");
        static readonly FieldInfo f_aircraft = AccessTools.Field(typeof(ConstantSpeedProp), "aircraft");

        static void Prefix(ConstantSpeedProp __instance)
        {
            if (!splitActive) return;
            var aircraft = f_aircraft.GetValue(__instance) as Aircraft;
            if (aircraft == null) return;
            var localAircraft = GetLocalAircraft();
            if (localAircraft == null || aircraft != localAircraft) return;
            var inputs = f_controlInputs.GetValue(__instance) as ControlInputs;
            if (inputs == null) return;

            Vector3 localPos = ((Component)aircraft).transform.InverseTransformPoint(__instance.transform.position);
            if (localPos.x < -0.5f) inputs.throttle = leftThrottle;
            else if (localPos.x > 0.5f) inputs.throttle = rightThrottle;
            else inputs.throttle = (leftThrottle + rightThrottle) * 0.5f;
        }

        static void Postfix(ConstantSpeedProp __instance)
        {
            if (!splitActive) return;
            var aircraft = f_aircraft.GetValue(__instance) as Aircraft;
            if (aircraft == null) return;
            var localAircraft = GetLocalAircraft();
            if (localAircraft == null || aircraft != localAircraft) return;
            var inputs = f_controlInputs.GetValue(__instance) as ControlInputs;
            if (inputs == null) return;
            inputs.throttle = (leftThrottle + rightThrottle) * 0.5f;
        }
    }

    [HarmonyPatch(typeof(PropFan), "FixedUpdate")]
    static class PropFanPatch
    {
        static readonly FieldInfo f_inputs = AccessTools.Field(typeof(PropFan), "inputs");
        static readonly FieldInfo f_aircraft = AccessTools.Field(typeof(PropFan), "aircraft");

        static void Prefix(PropFan __instance)
        {
            if (!splitActive) return;
            var aircraft = f_aircraft.GetValue(__instance) as Aircraft;
            if (aircraft == null) return;
            var localAircraft = GetLocalAircraft();
            if (localAircraft == null || aircraft != localAircraft) return;
            var inputs = f_inputs.GetValue(__instance) as ControlInputs;
            if (inputs == null) return;

            Vector3 localPos = ((Component)aircraft).transform.InverseTransformPoint(((Component)__instance).transform.position);
            if (localPos.x < -0.5f) inputs.throttle = leftThrottle;
            else if (localPos.x > 0.5f) inputs.throttle = rightThrottle;
            else inputs.throttle = (leftThrottle + rightThrottle) * 0.5f;
        }

        static void Postfix(PropFan __instance)
        {
            if (!splitActive) return;
            var aircraft = f_aircraft.GetValue(__instance) as Aircraft;
            if (aircraft == null) return;
            var localAircraft = GetLocalAircraft();
            if (localAircraft == null || aircraft != localAircraft) return;
            var inputs = f_inputs.GetValue(__instance) as ControlInputs;
            if (inputs == null) return;
            inputs.throttle = (leftThrottle + rightThrottle) * 0.5f;
        }
    }
}

public class SplitThrottleRunner : MonoBehaviour
{
    public static SplitThrottleRunner Instance;

    void Awake() { Instance = this; }
    void OnEnable() { StartCoroutine(UpdateLoop()); }

    IEnumerator UpdateLoop()
    {
        while (true)
        {
            yield return null;
            try { DoUpdate(); } catch { }
        }
    }

    void DoUpdate()
    {
        if (!Plugin.cfgEnabled.Value) return;
        if (GameManager.gameState != GameState.Multiplayer && GameManager.gameState != GameState.SinglePlayer) return;

        var aircraft = Plugin.GetLocalAircraft();
        bool wasActive = Plugin.splitActive;
        Plugin.splitActive = aircraft != null && !Plugin.IsDisabledAircraft(aircraft);

        if (!wasActive && Plugin.splitActive)
        {
            Plugin.leftThrottle = aircraft.GetInputs().throttle;
            Plugin.rightThrottle = Plugin.leftThrottle;
        }

        if (!Plugin.splitActive) return;

        // Rewired button controls
        try
        {
            var player = ReInput.players.GetPlayer(0);
            if (player != null)
            {
                float step = Plugin.cfgKeyboardStep.Value;
                if (player.GetButton(Plugin.ACT_LEFT_UP))
                    Plugin.leftThrottle = Mathf.Clamp01(Plugin.leftThrottle + step);
                if (player.GetButton(Plugin.ACT_LEFT_DOWN))
                    Plugin.leftThrottle = Mathf.Clamp01(Plugin.leftThrottle - step);
                if (player.GetButton(Plugin.ACT_RIGHT_UP))
                    Plugin.rightThrottle = Mathf.Clamp01(Plugin.rightThrottle + step);
                if (player.GetButton(Plugin.ACT_RIGHT_DOWN))
                    Plugin.rightThrottle = Mathf.Clamp01(Plugin.rightThrottle - step);

                if (player.GetButtonDown(Plugin.ACT_SYNC))
                {
                    Plugin.leftThrottle = aircraft.GetInputs().throttle;
                    Plugin.rightThrottle = Plugin.leftThrottle;
                }
            }
        }
        catch { }

        // HOTAS direct axis reading
        try
        {
            var player = ReInput.players.GetPlayer(0);
            if (player != null)
            {
                int leftIdx = Plugin.cfgLeftAxisIndex.Value;
                int rightIdx = Plugin.cfgRightAxisIndex.Value;
                string nameFilter = Plugin.cfgControllerName.Value.ToUpperInvariant();

                foreach (var joystick in player.controllers.Joysticks)
                {
                    if (!joystick.name.ToUpperInvariant().Contains(nameFilter)) continue;
                    if (leftIdx < joystick.axisCount && rightIdx < joystick.axisCount)
                    {
                        float leftRaw = joystick.Axes[leftIdx].valueRaw;
                        float rightRaw = joystick.Axes[rightIdx].valueRaw;

                        if (Plugin.cfgInvertLeft.Value) leftRaw = -leftRaw;
                        if (Plugin.cfgInvertRight.Value) rightRaw = -rightRaw;

                        Plugin.leftThrottle = Mathf.Clamp01(0.5f * (leftRaw + 1f));
                        Plugin.rightThrottle = Mathf.Clamp01(0.5f * (rightRaw + 1f));
                        break;
                    }
                }
            }
        }
        catch { }
    }

    void OnGUI()
    {
        if (!Plugin.cfgEnabled.Value || !Plugin.splitActive || !Plugin.cfgShowOverlay.Value) return;

        float w = 120, h = 80;
        float x = Screen.width - w - 10;
        float y = Screen.height / 2f - h / 2f;

        GUI.Box(new Rect(x, y, w, h), "");
        var titleStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontStyle = FontStyle.Bold, fontSize = 11 };
        GUI.Label(new Rect(x, y + 2, w, 16), "SPLIT THROTTLE", titleStyle);

        float barW = 30, barH = 50, barY = y + 22;
        DrawBar(x + 20, barY, barW, barH, Plugin.leftThrottle, "L");
        DrawBar(x + w - 20 - barW, barY, barW, barH, Plugin.rightThrottle, "R");
    }

    void DrawBar(float x, float y, float w, float h, float value, string label)
    {
        GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0, new Color(0.2f, 0.2f, 0.2f, 0.8f), 0, 0);
        float fillH = h * value;
        Color col = value < 0.8f ? Color.green : (value < 0.95f ? Color.yellow : Color.red);
        GUI.DrawTexture(new Rect(x, y + h - fillH, w, fillH), Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0, new Color(col.r, col.g, col.b, 0.8f), 0, 0);
        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 10 };
        GUI.Label(new Rect(x, y + h, w, 14), $"{label} {value * 100:0}%", style);
    }
}
