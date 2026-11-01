using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace OriVR
{
    // Build 5: OpenXR diorama. VR starts by itself once the game has a camera, unless VR.AutoStart is false, and if
    // OpenXR cannot start the game stays flat. Telemetry is stopped, because the lab keeps this game offline.
    [BepInPlugin("com.scubasteve2365.orivr", "OriVR", "0.0.5")]
    public class Plugin : BaseUnityPlugin
    {
        internal static BepInEx.Logging.ManualLogSource Log;
        internal static ConfigEntry<bool> BlockTelemetry;
        internal static int TelemetryBlocked;

        string commandFile;
        RenderTests tests;
        XrRig rig;
        internal static int Ticks;
        internal static bool RigActive { get { return instance != null && instance.rig.Active; } }
        static Plugin instance;
        ConfigEntry<bool> autoStart;
        ConfigEntry<float> stageWidth, stageDistance, separation;
        ConfigEntry<int> eyeWidth;
        bool triedAutoStart;
        bool[] tuneWas = new bool[6];
        int ticks;
        int nextCommandCheck;
        int autoProbeFrame = 600;

        void Awake()
        {
            Log = Logger;
            instance = this;
            BlockTelemetry = Config.Bind("Network", "BlockTelemetry", true, "Stop the game sending telemetry to its server.");
            commandFile = Path.Combine(Paths.GameRootPath, "vrlab_command.txt");
            tests = gameObject.AddComponent<RenderTests>();
            tests.ShotDir = Path.Combine(Paths.GameRootPath, "vrlab_shots");
            rig = gameObject.AddComponent<XrRig>();
            rig.PluginDir = Path.GetDirectoryName(Info.Location);
            rig.ShotDir = tests.ShotDir;
            autoStart = Config.Bind("VR", "AutoStart", true, "Start VR by itself once the game has a camera.");
            stageWidth = Config.Bind("VR", "StageWidth", 2.0f, "Width of the stage in metres. Hold both stick clicks: X smaller, Y larger.");
            stageDistance = Config.Bind("VR", "StageDistance", 2.0f, "Distance to the stage in metres. Hold both stick clicks: A closer, B further.");
            separation = Config.Bind("VR", "Separation", 1.0f, "Depth strength, 1 is natural. Hold both stick clicks: LB less, RB more.");
            eyeWidth = Config.Bind("VR", "EyeWidth", 2560, "Eye image width in pixels. Height follows at 16:9.");
            rig.StageWidth = stageWidth.Value;
            rig.StageDistance = stageDistance.Value;
            rig.Separation = separation.Value;
            rig.EyeWidth = eyeWidth.Value;
            rig.EyeHeight = eyeWidth.Value * 9 / 16;
            new Harmony("com.scubasteve2365.orivr").PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo("OriVR build 5 loaded. Unity " + Application.unityVersion + ", pointer size " + IntPtr.Size);
        }

        void FixedUpdate() { ticks++; Ticks = ticks; }

        void Update()
        {
            if (Time.frameCount >= nextCommandCheck)
            {
                nextCommandCheck = Time.frameCount + 15;
                ReadCommands();
            }
            if (!triedAutoStart && autoStart.Value && Time.frameCount >= 300 && Game.UI.Cameras.Current != null)
            {
                triedAutoStart = true;
                rig.Begin();
            }
            if (rig.Active) Tune();
            if (autoProbeFrame > 0 && Time.frameCount >= autoProbeFrame)
            {
                autoProbeFrame = 0;
                Probe();
            }
        }

        // A stand-in until the in-headset menu exists: hold both stick clicks and press a button. Changes are saved.
        void Tune()
        {
            bool held = SmartInput.XboxControllerInput.GetButton(SmartInput.XboxControllerInput.Button.LeftStick)
                     && SmartInput.XboxControllerInput.GetButton(SmartInput.XboxControllerInput.Button.RightStick);
            var keys = new[]
            {
                SmartInput.XboxControllerInput.Button.ButtonA, SmartInput.XboxControllerInput.Button.ButtonB,
                SmartInput.XboxControllerInput.Button.ButtonX, SmartInput.XboxControllerInput.Button.ButtonY,
                SmartInput.XboxControllerInput.Button.LeftShoulder, SmartInput.XboxControllerInput.Button.RightShoulder
            };
            for (int i = 0; i < keys.Length; i++)
            {
                bool down = held && SmartInput.XboxControllerInput.GetButton(keys[i]);
                if (down && !tuneWas[i]) TuneStep(i);
                tuneWas[i] = down;
            }
        }

        void TuneStep(int i)
        {
            if (i == 0) stageDistance.Value = Mathf.Max(0.5f, stageDistance.Value - 0.1f);
            if (i == 1) stageDistance.Value = Mathf.Min(10f, stageDistance.Value + 0.1f);
            if (i == 2) stageWidth.Value = Mathf.Max(0.5f, stageWidth.Value - 0.1f);
            if (i == 3) stageWidth.Value = Mathf.Min(10f, stageWidth.Value + 0.1f);
            if (i == 4) separation.Value = Mathf.Max(0f, separation.Value - 0.1f);
            if (i == 5) separation.Value = Mathf.Min(3f, separation.Value + 0.1f);
            rig.StageWidth = stageWidth.Value;
            rig.StageDistance = stageDistance.Value;
            rig.Separation = separation.Value;
            rig.ApplyStage();
            Log.LogInfo(string.Format("TUNE stage {0:F1} m wide at {1:F1} m, separation {2:F1}", stageWidth.Value, stageDistance.Value, separation.Value));
        }

        void ReadCommands()
        {
            if (!File.Exists(commandFile)) return;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(commandFile);
                File.Delete(commandFile);
            }
            catch (IOException) { return; } // the harness is still writing it: try again next check
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                Log.LogInfo("command: " + line);
                if (line == "probe") Probe();
                else if (line == "compare") tests.Compare();
                else if (line == "compare2") tests.CompareSameSway();
                else if (line == "vr on") rig.Begin();
                else if (line == "vr off") rig.End();
                else if (line == "xrshot") rig.Shot();
                else if (line == "force 0" || line == "force 1") rig.ForceRender = line == "force 1";
                else if (line == "flip 0" || line == "flip 1") rig.FlipY = line == "flip 1";
                else if (line == "stamp 0" || line == "stamp 1") rig.Stamp = line == "stamp 1";
                else if (line == "freeze 1" || line == "freeze 0")
                {
                    RenderTests.FreezeTurbulenceSetting = line == "freeze 1";
                    Log.LogInfo("freeze turbulence during our renders: " + RenderTests.FreezeTurbulenceSetting);
                }
                else if (line.StartsWith("stereo "))
                {
                    float sep;
                    try { sep = float.Parse(line.Substring(7), System.Globalization.CultureInfo.InvariantCulture); }
                    catch (System.FormatException) { Log.LogWarning("bad number: " + line); continue; }
                    tests.Stereo(sep);
                }
                else Log.LogWarning("unknown command: " + line);
            }
        }

        void Probe()
        {
            Log.LogInfo(string.Format("PROBE frame {0} ticks {1} time {2:F2} fixedDelta {3:F5} maxDelta {4:F5} targetFps {5} vsync {6} screen {7}x{8} telemetryBlocked {9}",
                Time.frameCount, ticks, Time.time, Time.fixedDeltaTime, Time.maximumDeltaTime, Application.targetFrameRate,
                QualitySettings.vSyncCount, Screen.width, Screen.height, TelemetryBlocked));
            foreach (var cam in Camera.allCameras)
            {
                var p = cam.transform.position;
                Log.LogInfo(string.Format("CAM {0} depth {1} ortho {2} fov {3:F2} aspect {4:F3} near {5} far {6} pos ({7:F2},{8:F2},{9:F2}) rot {10} mask {11:X8} clear {12} target {13}",
                    cam.name, cam.depth, cam.orthographic, cam.fieldOfView, cam.aspect, cam.nearClipPlane, cam.farClipPlane,
                    p.x, p.y, p.z, cam.transform.rotation.eulerAngles, cam.cullingMask, cam.clearFlags,
                    cam.targetTexture != null ? cam.targetTexture.name : "screen"));
            }
            var cur = Game.UI.Cameras.Current;
            if (cur != null)
            {
                var c = cur.Camera;
                Log.LogInfo("GAMEPLAY state " + (GameStateMachine.Instance != null ? GameStateMachine.Instance.CurrentState.ToString() : "none") + " ori " + (Game.Characters.Current != null ? Game.Characters.Current.Position.ToString() : "none") + " camera " + c.name + " widthUnits " + cur.CameraWidthWorldUnits.ToString("F2") + " bounds " + cur.CameraBoundingBox);
                Log.LogInfo("VIEW\n" + c.worldToCameraMatrix);
                Log.LogInfo("PROJ\n" + c.projectionMatrix);
            }
            else Log.LogInfo("GAMEPLAY camera not set yet");
        }
    }

    // SteamTelemetry.Awake sends an event at once, then deaths and achievements follow (decomp SteamTelemetry.cs).
    // Steam achievements and stats go through Steam and are not touched by this.
    [HarmonyPatch(typeof(SteamTelemetry), "Send")]
    static class TelemetryPatch
    {
        static bool Prefix()
        {
            if (!Plugin.BlockTelemetry.Value) return true;
            Plugin.TelemetryBlocked++;
            return false;
        }
    }

    // TurbulenceManager.OnRenderObject moves the plant sway on by real time at every render of the main camera
    // (decomp TurbulenceManager.cs). The second eye must see the sway the first eye saw, so our extra renders skip it.
    [HarmonyPatch(typeof(TurbulenceManager), "OnRenderObject")]
    static class TurbulencePatch
    {
        static bool Prefix() { return !RenderTests.SkipTurbulenceNow && !(XrRig.SuppressFlat && !XrRig.InEyeRender); }
    }

    // With VR on, the flat render draws nothing and post-processing is skipped, so the eyes can use their own size.
    [HarmonyPatch(typeof(UberPostProcess), "OnPreRender")]
    static class FlatPreRenderPatch
    {
        static bool Prefix(UberPostProcess __instance) { return __instance.IsSubCam || !XrRig.SuppressFlat || XrRig.InEyeRender; }
    }

    [HarmonyPatch(typeof(UberPostProcess), "OnRenderImage")]
    static class FlatImagePatch
    {
        static bool Prefix(UberPostProcess __instance, RenderTexture source, RenderTexture destination)
        {
            if (__instance.IsSubCam || !XrRig.SuppressFlat || XrRig.InEyeRender) return true;
            Graphics.Blit(source, destination);
            return false;
        }
    }

    // SteamVR takes window focus. The game would pause and mute, so in VR it is told it still has focus.
    [HarmonyPatch(typeof(GameController), "OnApplicationFocus")]
    static class FocusPatch
    {
        static void Prefix(ref bool focusStatus)
        {
            if (Plugin.RigActive) focusStatus = true;
        }
    }
}
