using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace OriVR
{
    // Renders the game camera once per eye from one game tick and hands both images to OriVRNative.
    //
    // The diorama: a flat rectangle fixed in the room (the stage) shows what the flat game shows at z 0. Each eye looks
    // through that rectangle from its own place, so z 0 stays at the stage, background sits behind and foreground in front.
    internal class XrRig : MonoBehaviour
    {
        const string Dll = "OriVRNative";
        const int EventInit = 1, EventSubmit = 2, EventShutdown = 3, EventResize = 4;

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct EyeView { public float px, py, pz, qx, qy, qz, qw, left, right, up, down; }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct FrameInfo
        {
            public int running, shouldRender, viewsValid, sessionState;
            public float periodMs;
            public EyeView eye0, eye1;
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)] static extern IntPtr LoadLibraryA(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);

        // Unity's Mono will not bind a DllImport to a DLL in the plugin folder, so the functions are fetched by hand.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr GetRenderEventFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int StateFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetStageFn(float cx, float cy, float cz, float width, float height, float qy, float qw);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetSizeFn(int width, int height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetSharpenFn(float amount);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DumpNextFn([MarshalAs(UnmanagedType.LPStr)] string basePath);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int RecommendedWidthFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void StartFn(IntPtr left, IntPtr right, int width, int height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int BeginFrameFn(out FrameInfo info, IntPtr left, IntPtr right);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PopLogFn(StringBuilder buffer, int size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void StatsFn(out uint frames, out uint layers, out uint skipped);

        GetRenderEventFn OriXR_GetRenderEvent;
        StateFn OriXR_State;
        SetStageFn OriXR_SetStage;
        SetSizeFn OriXR_SetSize;
        SetSharpenFn OriXR_SetSharpen;
        DumpNextFn OriXR_DumpNext;
        RecommendedWidthFn OriXR_RecommendedWidth;
        StartFn OriXR_Start;
        BeginFrameFn OriXR_BeginFrame;
        PopLogFn OriXR_PopLog;
        StatsFn OriXR_Stats;

        static T Fn<T>(IntPtr module, string name) where T : class
        {
            var p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        internal string PluginDir;
        internal string ShotDir;
        internal float StageWidth = 2.0f, StageDistance = 2.0f, Separation = 1.0f;
        internal float Sharpness = 1.0f;     // resolution scale: 1 matches the headset's pixel density across the stage
        internal float PopOut;               // 0 keeps the gameplay plane on the stage. Above 0, a deeper layer sits there.
        internal float Sharpen = 0.3f;       // strength of the sharpen pass, 0 is off
        internal bool StereoFixes = true;    // lab switch: the second-eye fixes below
        internal bool MatchRefresh = true;   // run one game tick per headset frame

        // Where the viewer was when the stage was placed, and which way they faced. Eye poses are read relative to this.
        Vector3 viewPoint;
        Quaternion viewTurn = Quaternion.identity;
        bool wantRecenter = true;
        float eyeTanSpan;  // width of the headset's eye view on a flat plane one metre away
        float savedFixed = -1f, savedMax;
        internal int EyeWidth = 2560, EyeHeight = 1440;
        internal bool FlipY = true, Stamp, Mirror = true;
        internal bool ForceRender;  // lab test: keep rendering eyes when the runtime is not asking for images

        // While these hold, the game camera's own flat render draws nothing and skips post-processing. The camera stays
        // enabled because SoundController2D and GUIController read Camera.main, which is null for a disabled camera.
        internal static bool SuppressFlat, InEyeRender;

        bool nativeLoaded, active, wantShot, loggedPose;
        int savedMask = -1;
        IntPtr renderEvent;
        RenderTexture[] eyeRender = new RenderTexture[2];
        RenderTexture[] eyeOut = new RenderTexture[2];
        UberAlphaBuffer[] eyeAlpha = new UberAlphaBuffer[2];
        // During a cross fade the game alternates two views, so each eye keeps one buffer per view.
        UberAlphaBuffer[,] fadeAlpha = new UberAlphaBuffer[2, 2];
        Material copyMat;
        Texture2D stampTex;
        Camera cam;
        int savedTargetFps, savedVsync;
        readonly StringBuilder logBuf = new StringBuilder(512);
        uint renderedPairs;
        int lastState = -1;

        internal bool Active { get { return active; } }

        bool LoadNative()
        {
            if (nativeLoaded) return true;
            var path = Path.Combine(PluginDir, Dll + ".dll");
            var module = LoadLibraryA(path);
            if (module == IntPtr.Zero)
            {
                Plugin.Log.LogError("could not load " + path + ", error " + Marshal.GetLastWin32Error());
                return false;
            }
            OriXR_GetRenderEvent = Fn<GetRenderEventFn>(module, "OriXR_GetRenderEvent");
            OriXR_State = Fn<StateFn>(module, "OriXR_State");
            OriXR_SetStage = Fn<SetStageFn>(module, "OriXR_SetStage");
            OriXR_SetSize = Fn<SetSizeFn>(module, "OriXR_SetSize");
            OriXR_SetSharpen = Fn<SetSharpenFn>(module, "OriXR_SetSharpen");
            OriXR_DumpNext = Fn<DumpNextFn>(module, "OriXR_DumpNext");
            OriXR_RecommendedWidth = Fn<RecommendedWidthFn>(module, "OriXR_RecommendedWidth");
            OriXR_Start = Fn<StartFn>(module, "OriXR_Start");
            OriXR_BeginFrame = Fn<BeginFrameFn>(module, "OriXR_BeginFrame");
            OriXR_PopLog = Fn<PopLogFn>(module, "OriXR_PopLog");
            OriXR_Stats = Fn<StatsFn>(module, "OriXR_Stats");
            renderEvent = OriXR_GetRenderEvent();
            nativeLoaded = true;
            return true;
        }

        internal void Begin()
        {
            if (active || !LoadNative()) return;
            var cur = Game.UI.Cameras.Current;
            if (cur == null) { Plugin.Log.LogWarning("vr on: no gameplay camera yet"); return; }
            cam = cur.Camera;

            var shader = Shader.Find("Hidden/BlitCopy");
            if (shader == null) { Plugin.Log.LogError("no Hidden/BlitCopy shader"); return; }
            copyMat = new Material(shader);
            for (int i = 0; i < 2; i++)
            {
                eyeRender[i] = new RenderTexture(EyeWidth, EyeHeight, 24, RenderTextureFormat.ARGB32);
                eyeRender[i].name = "OriVR eye render " + i;
                eyeRender[i].Create();
                eyeOut[i] = new RenderTexture(EyeWidth, EyeHeight, 0, RenderTextureFormat.ARGB32);
                eyeOut[i].name = "OriVR eye out " + i;
                eyeOut[i].Create();
                eyeAlpha[i] = new UberAlphaBuffer();
                fadeAlpha[i, 0] = new UberAlphaBuffer();
                fadeAlpha[i, 1] = new UberAlphaBuffer();
            }
            stampTex = new Texture2D(32, 1, TextureFormat.RGB24, false);
            stampTex.filterMode = FilterMode.Point;

            savedTargetFps = Application.targetFrameRate;
            savedVsync = QualitySettings.vSyncCount;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;  // xrWaitFrame paces the game now

            ApplyStage();
            OriXR_Start(eyeOut[0].GetNativeTexturePtr(), eyeOut[1].GetNativeTexturePtr(), EyeWidth, EyeHeight);
            GL.IssuePluginEvent(renderEvent, EventInit);
            active = true;
            Plugin.Log.LogInfo(string.Format("VR starting: eye {0}x{1}, stage {2} m wide at {3} m, separation x{4}", EyeWidth, EyeHeight, StageWidth, StageDistance, Separation));
            StartCoroutine(Loop());
        }

        internal void End()
        {
            if (!active) return;
            active = false;
            ShowFlat();
            if (savedFixed >= 0f)
            {
                Time.fixedDeltaTime = savedFixed;
                Time.maximumDeltaTime = savedMax;
                savedFixed = -1f;
            }
            Application.targetFrameRate = savedTargetFps;
            QualitySettings.vSyncCount = savedVsync;
            GL.IssuePluginEvent(renderEvent, EventShutdown);
            Plugin.Log.LogInfo("VR stopped after " + renderedPairs + " eye pairs");
        }

        internal void Shot() { wantShot = true; }

        internal void Recenter() { wantRecenter = true; }

        internal void Dump(string basePath) { if (nativeLoaded) OriXR_DumpNext(basePath); }

        // Lab: renders the next frame with both eyes in the same place. The two images must then match exactly,
        // so any difference is an effect that behaves differently for the second eye.
        // They are held together for a few frames first, because each eye's alpha buffer comes from its previous frame.
        internal void EyeCheck() { eyeCheckFrames = 4; }
        int eyeCheckFrames;

        static readonly System.Reflection.FieldInfo DistortField = HarmonyLib.AccessTools.Field(typeof(UberPostProcess), "m_renderDistort");
        static readonly System.Reflection.FieldInfo ZonesField = HarmonyLib.AccessTools.Field(typeof(UberPostProcess), "m_renderZones");
        static readonly System.Reflection.FieldInfo LastPosField = HarmonyLib.AccessTools.Field(typeof(UberPostProcess), "m_lastPos");
        internal static int EyeIndex;

        internal int WantedWidth()
        {
            int rec = OriXR_RecommendedWidth != null ? OriXR_RecommendedWidth() : 0;
            if (rec <= 0 || eyeTanSpan <= 0f) return EyeWidth;
            // The stage is flat, so pixel density across it compares directly with the headset's flat eye image.
            int w = Mathf.RoundToInt(rec * (StageWidth / StageDistance) / eyeTanSpan * Sharpness);
            return Mathf.Clamp(w / 16 * 16, 1280, 6144);
        }

        // Rebuilds the eye textures when the stage or sharpness calls for a size more than 3 percent away.
        internal void UpdateResolution()
        {
            if (!active || OriXR_State() != 2) return;
            int w = WantedWidth();
            if (Mathf.Abs(w - EyeWidth) <= EyeWidth * 0.03f) return;
            Resize(w);
        }

        void Resize(int width)
        {
            int height = width * 9 / 16;
            for (int i = 0; i < 2; i++)
            {
                var r = eyeRender[i];
                var o = eyeOut[i];
                eyeRender[i] = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                eyeRender[i].name = "OriVR eye render " + i;
                eyeRender[i].Create();
                eyeOut[i] = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
                eyeOut[i].name = "OriVR eye out " + i;
                eyeOut[i].Create();
                Destroy(r);  // frames already queued hold their own reference to the old texture
                Destroy(o);
            }
            EyeWidth = width;
            EyeHeight = height;
            OriXR_SetSize(width, height);
            GL.IssuePluginEvent(renderEvent, EventResize);
            ApplyStage();
            Plugin.Log.LogInfo(string.Format("RESOLUTION eye {0}x{1}, stage {2:F2} m at {3:F2} m, sharpness {4:F2}", width, height, StageWidth, StageDistance, Sharpness));
        }

        void MatchTick(float periodMs)
        {
            if (!MatchRefresh || periodMs <= 0f) return;
            float period = periodMs / 1000f;
            if (Mathf.Abs(Time.fixedDeltaTime - period) < 0.00002f) return;
            if (savedFixed < 0f)
            {
                savedFixed = Time.fixedDeltaTime;
                savedMax = Time.maximumDeltaTime;
            }
            Time.fixedDeltaTime = period;
            Time.maximumDeltaTime = period;
            Plugin.Log.LogInfo(string.Format("REFRESH headset {0:F2} Hz, one game tick per frame", 1f / period));
        }

        void TakeView(FrameInfo info)
        {
            var a = new Vector3(info.eye0.px, info.eye0.py, info.eye0.pz);
            var b = new Vector3(info.eye1.px, info.eye1.py, info.eye1.pz);
            viewPoint = (a + b) * 0.5f;
            var forward = new Quaternion(info.eye0.qx, info.eye0.qy, info.eye0.qz, info.eye0.qw) * new Vector3(0f, 0f, -1f);
            viewTurn = Quaternion.Euler(0f, Mathf.Atan2(-forward.x, -forward.z) * Mathf.Rad2Deg, 0f);
            eyeTanSpan = Mathf.Tan(info.eye0.right) - Mathf.Tan(info.eye0.left);
            ApplyStage();
            Plugin.Log.LogInfo("headset recommends " + OriXR_RecommendedWidth() + " wide over " + eyeTanSpan.ToString("F2") + " of flat view");
            Plugin.Log.LogInfo(string.Format("RECENTER view ({0:F2},{1:F2},{2:F2}), turned {3:F0} degrees", viewPoint.x, viewPoint.y, viewPoint.z, viewTurn.eulerAngles.y));
        }

        void ShowFlat()
        {
            SuppressFlat = false;
            if (cam != null && savedMask != -1) cam.cullingMask = savedMask;
            savedMask = -1;
        }

        internal void ApplyStage()
        {
            if (!nativeLoaded) return;
            OriXR_SetSharpen(Sharpen);
            var c = viewPoint + viewTurn * new Vector3(0f, 0f, -StageDistance);
            OriXR_SetStage(c.x, c.y, c.z, StageWidth, StageWidth * EyeHeight / EyeWidth, viewTurn.y, viewTurn.w);
        }

        void Update()
        {
            if (!nativeLoaded) return;
            while (OriXR_PopLog(logBuf, logBuf.Capacity) != 0)
            {
                Plugin.Log.LogInfo("XR: " + logBuf);
                logBuf.Length = 0;
            }
            logBuf.Length = 0;
        }

        IEnumerator Loop()
        {
            var wait = new WaitForEndOfFrame();
            float nextStat = Time.realtimeSinceStartup + 5f;
            int ticksAtStat = Plugin.Ticks, framesAtStat = Time.frameCount;
            while (active)
            {
                yield return wait;
                if (!active) break;
                var cur = Game.UI.Cameras.Current;
                if (cur == null) continue;
                if (cam != cur.Camera)
                {
                    ShowFlat();
                    cam = cur.Camera;
                }

                int state = OriXR_State();
                if (state != lastState)
                {
                    lastState = state;
                    Plugin.Log.LogInfo("XR native state " + state);
                }
                if (state == 3) { End(); yield break; }
                if (state != 2) continue;

                for (int i = 0; i < 2; i++)
                {
                    if (!eyeRender[i].IsCreated()) eyeRender[i].Create();
                    if (!eyeOut[i].IsCreated()) eyeOut[i].Create();
                }
                FrameInfo info;
                bool frame = OriXR_BeginFrame(out info, eyeOut[0].GetNativeTexturePtr(), eyeOut[1].GetNativeTexturePtr()) != 0;
                if (!frame)
                {
                    // Session idle: leave the flat game running.
                    ShowFlat();
                    continue;
                }
                MatchTick(info.periodMs);
                if (info.viewsValid != 0 && wantRecenter)
                {
                    wantRecenter = false;
                    TakeView(info);
                    UpdateResolution();
                }
                if (info.shouldRender != 0 || ForceRender) RenderEyes(cur, info);
                else ShowFlat();
                GL.IssuePluginEvent(renderEvent, EventSubmit);

                float now = Time.realtimeSinceStartup;
                if (now >= nextStat)
                {
                    uint frames, layers, skipped;
                    OriXR_Stats(out frames, out layers, out skipped);
                    float span = now - (nextStat - 5f);
                    Plugin.Log.LogInfo(string.Format("XRSTAT state {0} shouldRender {1} period {2:F2} ms, unity {3:F1} fps, ticks {4:F1}/s, submitted {5}, with images {6}, skipped {7}, pairs {8}",
                        info.sessionState, info.shouldRender, info.periodMs, (Time.frameCount - framesAtStat) / span, (Plugin.Ticks - ticksAtStat) / span, frames, layers, skipped, renderedPairs + " eye " + EyeWidth));
                    nextStat = now + 5f;
                    ticksAtStat = Plugin.Ticks;
                    framesAtStat = Time.frameCount;
                }
            }
        }

        void RenderEyes(GameplayCamera cur, FrameInfo info)
        {
            if (info.viewsValid != 0 && !loggedPose)
            {
                loggedPose = true;
                Plugin.Log.LogInfo(string.Format("XRVIEW left pos ({0:F3},{1:F3},{2:F3}) fov L {3:F3} R {4:F3} U {5:F3} D {6:F3}; right pos ({7:F3},{8:F3},{9:F3}) fov L {10:F3} R {11:F3} U {12:F3} D {13:F3}",
                    info.eye0.px, info.eye0.py, info.eye0.pz, info.eye0.left, info.eye0.right, info.eye0.up, info.eye0.down,
                    info.eye1.px, info.eye1.py, info.eye1.pz, info.eye1.left, info.eye1.right, info.eye1.up, info.eye1.down));
            }
            if (cam.cullingMask != 0) savedMask = cam.cullingMask;
            SuppressFlat = true;
            InEyeRender = true;
            if (savedMask != -1) cam.cullingMask = savedMask;
            var post = cur.CameraPostProcessing.UberPostProcess;
            var savedAlpha = post.CurrentAlphaBuffer;
            bool crossFading = Game.UI.Cameras.System != null && Game.UI.Cameras.System.CrossFadeManager.IsCrossFading;
            var view = cam.worldToCameraMatrix;
            float dist = -cam.transform.position.z;
            float unitsPerMetre = cur.CameraWidthWorldUnits / StageWidth;
            float planeZ = PopOut * dist;
            bool checking = eyeCheckFrames > 0;
            bool compareNow = eyeCheckFrames == 1;
            if (eyeCheckFrames > 0) eyeCheckFrames--;

            // The game queues these once per frame and clears them in the first render, so the second eye gets a copy.
            var distort = StereoFixes ? new System.Collections.Generic.List<MeshFilter>((System.Collections.Generic.List<MeshFilter>)DistortField.GetValue(post)) : null;
            var zones = StereoFixes ? new System.Collections.Generic.List<UberMotionBlurInterestZone>((System.Collections.Generic.List<UberMotionBlurInterestZone>)ZonesField.GetValue(post)) : null;
            // Camera velocity is worked out from the last render's position, so the second eye starts from the same place.
            object lastPos = LastPosField.GetValue(post);

            for (int i = 0; i < 2; i++)
            {
                EyeIndex = i;
                if (i == 1 && StereoFixes)
                {
                    DistortField.SetValue(post, distort);
                    ZonesField.SetValue(post, zones);
                    LastPosField.SetValue(post, lastPos);
                }
                var e = i == 0 ? info.eye0 : info.eye1;
                // Without valid tracking, stand at the stage's normal viewing spot with a fixed eye gap.
                var local = checking ? Vector3.zero : info.viewsValid != 0
                    ? Quaternion.Inverse(viewTurn) * (new Vector3(e.px, e.py, e.pz) - viewPoint)
                    : new Vector3(i == 0 ? -0.0315f : 0.0315f, 0f, 0f);
                float x = local.x * unitsPerMetre * Separation;
                float y = local.y * unitsPerMetre * Separation;
                float eyeDist = Mathf.Max(dist + local.z * unitsPerMetre, dist * 0.25f);

                cam.worldToCameraMatrix = Matrix4x4.TRS(new Vector3(-x, -y, -(eyeDist - dist)), Quaternion.identity, Vector3.one) * view;
                cam.projectionMatrix = RenderTests.EyeProjection(cam, x, y, eyeDist, planeZ);
                var alpha = crossFading ? fadeAlpha[i, CrossFadePatch.CurrentView(Game.UI.Cameras.System.UberPostProcessingCrossFade)] : eyeAlpha[i];
                post.CurrentAlphaBuffer = alpha;
                alpha.SetCurrentAlphaGrab();
                // Plant sway moves on after the last eye only, so both eyes draw the same sway.
                RenderTests.SkipTurbulenceNow = i == 0;
                cam.targetTexture = eyeRender[i];
                cam.Render();
                cam.targetTexture = null;
                RenderTests.SkipTurbulenceNow = false;
                Finish(i);
            }
            cam.ResetWorldToCameraMatrix();
            cam.ResetProjectionMatrix();
            cam.cullingMask = 0;
            InEyeRender = false;
            post.CurrentAlphaBuffer = savedAlpha;
            renderedPairs++;

            if (Mirror) Graphics.Blit(eyeRender[0], (RenderTexture)null);
            if (wantShot)
            {
                wantShot = false;
                SaveEyes();
            }
            if (compareNow) CompareEyes();
        }

        // Copies the eye image to the texture the native side sends on, upside down if asked, with the frame number.
        void Finish(int i)
        {
            var old = RenderTexture.active;
            RenderTexture.active = eyeOut[i];
            copyMat.mainTexture = eyeRender[i];
            GL.PushMatrix();
            GL.LoadOrtho();
            copyMat.SetPass(0);
            float top = FlipY ? 0f : 1f, bottom = 1f - top;
            GL.Begin(7);  // GL.QUADS
            GL.TexCoord2(0f, bottom); GL.Vertex3(0f, 0f, 0f);
            GL.TexCoord2(0f, top); GL.Vertex3(0f, 1f, 0f);
            GL.TexCoord2(1f, top); GL.Vertex3(1f, 1f, 0f);
            GL.TexCoord2(1f, bottom); GL.Vertex3(1f, 0f, 0f);
            GL.End();
            if (Stamp)
            {
                // 32 blocks, least significant bit first, along the image's lower left edge.
                int frame = Time.frameCount;
                for (int b = 0; b < 32; b++) stampTex.SetPixel(b, 0, ((frame >> b) & 1) != 0 ? Color.white : Color.black);
                stampTex.Apply();
                copyMat.mainTexture = stampTex;
                copyMat.SetPass(0);
                float w = 32f * 12f / eyeOut[i].width, h = 12f / eyeOut[i].height;
                GL.Begin(7);
                GL.TexCoord2(0f, 0f); GL.Vertex3(0f, 0f, 0f);
                GL.TexCoord2(0f, 1f); GL.Vertex3(0f, h, 0f);
                GL.TexCoord2(1f, 1f); GL.Vertex3(w, h, 0f);
                GL.TexCoord2(1f, 0f); GL.Vertex3(w, 0f, 0f);
                GL.End();
            }
            GL.PopMatrix();
            RenderTexture.active = old;
        }

        void CompareEyes()
        {
            var old = RenderTexture.active;
            var pixels = new Color32[2][];
            for (int i = 0; i < 2; i++)
            {
                RenderTexture.active = eyeOut[i];
                var tex = new Texture2D(eyeOut[i].width, eyeOut[i].height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                tex.Apply();
                pixels[i] = tex.GetPixels32();
                Directory.CreateDirectory(ShotDir);
                File.WriteAllBytes(Path.Combine(ShotDir, "check_" + i + ".png"), tex.EncodeToPNG());
                Destroy(tex);
            }
            RenderTexture.active = old;
            int differing = 0, max = 0;
            for (int i = 0; i < pixels[0].Length; i++)
            {
                var a = pixels[0][i];
                var b = pixels[1][i];
                int d = Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));
                if (d > 0) differing++;
                if (d > max) max = d;
            }
            Plugin.Log.LogInfo(string.Format("EYECHECK frame {0}, fixes {1}, cross fading {2}: differing pixels {3} of {4}, max {5}", Time.frameCount, StereoFixes,
                Game.UI.Cameras.System != null && Game.UI.Cameras.System.CrossFadeManager.IsCrossFading, differing, pixels[0].Length, max));
        }

        void SaveEyes()
        {
            Directory.CreateDirectory(ShotDir);
            var old = RenderTexture.active;
            for (int i = 0; i < 2; i++)
            {
                RenderTexture.active = eyeOut[i];
                var tex = new Texture2D(eyeOut[i].width, eyeOut[i].height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(Path.Combine(ShotDir, "xr_eye_" + i + ".png"), tex.EncodeToPNG());
                Destroy(tex);
            }
            RenderTexture.active = old;
            Plugin.Log.LogInfo("XRSHOT saved at frame " + Time.frameCount);
        }
    }
}
