using System.Collections;
using System.IO;
using UnityEngine;

namespace OriVR
{
    // One-shot tests, started from the command file. They render the game camera into textures at the end of a
    // frame and put everything back afterwards, so the picture on screen is untouched.
    internal class RenderTests : MonoBehaviour
    {
        internal static bool FreezeTurbulenceSetting = true;
        internal static bool SkipTurbulenceNow;
        internal string ShotDir;
        internal bool Busy;

        internal void Compare()
        {
            if (!Busy) StartCoroutine(CompareRoutine());
        }

        internal void Stereo(float separation)
        {
            if (!Busy) StartCoroutine(StereoRoutine(separation));
        }

        Camera GameCamera()
        {
            var cur = Game.UI.Cameras.Current;
            if (cur == null) Plugin.Log.LogWarning("no gameplay camera");
            return cur == null ? null : cur.Camera;
        }

        // Renders into a texture and returns the pixels. The caller sets any matrices first.
        Texture2D Grab(Camera cam, RenderTexture rt)
        {
            var old = RenderTexture.active;
            cam.targetTexture = rt;
            SkipTurbulenceNow = FreezeTurbulenceSetting;
            cam.Render();
            SkipTurbulenceNow = false;
            cam.targetTexture = null;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = old;
            return tex;
        }

        void Diff(string name, Texture2D a, Texture2D b)
        {
            var pa = a.GetPixels32();
            var pb = b.GetPixels32();
            int differing = 0, max = 0;
            long sum = 0;
            for (int i = 0; i < pa.Length; i++)
            {
                int d = Mathf.Max(Mathf.Abs(pa[i].r - pb[i].r), Mathf.Max(Mathf.Abs(pa[i].g - pb[i].g), Mathf.Abs(pa[i].b - pb[i].b)));
                if (d > 0) differing++;
                if (d > max) max = d;
                sum += d;
            }
            Plugin.Log.LogInfo(string.Format("DIFF {0}: differing pixels {1} of {2}, max {3}, mean {4:F4}", name, differing, pa.Length, max, sum / (double)pa.Length));
        }

        void Save(string name, Texture2D tex)
        {
            Directory.CreateDirectory(ShotDir);
            File.WriteAllBytes(Path.Combine(ShotDir, name + ".png"), tex.EncodeToPNG());
        }

        IEnumerator CompareRoutine()
        {
            Busy = true;
            yield return new WaitForEndOfFrame();
            var cam = GameCamera();
            if (cam != null)
            {
                var rt = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32);
                int frame = Time.frameCount;
                var view = cam.worldToCameraMatrix;
                var proj = cam.projectionMatrix;

                var a = Grab(cam, rt);
                var b = Grab(cam, rt);
                var c = Grab(cam, rt);
                // The zero-difference control: our own matrices, holding exactly the values the game has.
                cam.worldToCameraMatrix = view;
                cam.projectionMatrix = proj;
                var d = Grab(cam, rt);
                cam.ResetWorldToCameraMatrix();
                cam.ResetProjectionMatrix();

                Plugin.Log.LogInfo("COMPARE frame " + frame + " same frame after " + (Time.frameCount == frame) + " freeze " + FreezeTurbulenceSetting);
                Diff("render1_vs_render2", a, b);
                Diff("render2_vs_render3", b, c);
                Diff("render3_vs_ownMatrices", c, d);
                Save("compare_1", a);
                Save("compare_2", b);
                Save("compare_3", c);
                Save("compare_own", d);
                Destroy(a); Destroy(b); Destroy(c); Destroy(d);
                rt.Release();
                Destroy(rt);
            }
            Busy = false;
        }

        // Off-axis projection through the rectangle the flat game shows at z 0, seen from an eye moved sideways.
        internal static Matrix4x4 EyeProjection(Camera cam, float eyeX)
        {
            float dist = -cam.transform.position.z; // the gameplay plane is z 0
            float n = cam.nearClipPlane;
            float halfH = dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float halfW = halfH * cam.aspect;
            float scale = n / dist;
            float l = (-halfW - eyeX) * scale, r = (halfW - eyeX) * scale;
            float b = -halfH * scale, t = halfH * scale;
            var flat = cam.projectionMatrix;
            var m = Matrix4x4.zero;
            m[0, 0] = 2f * n / (r - l);
            m[0, 2] = (r + l) / (r - l);
            m[1, 1] = 2f * n / (t - b);
            m[1, 2] = (t + b) / (t - b);
            m[2, 2] = flat[2, 2];
            m[2, 3] = flat[2, 3];
            m[3, 2] = -1f;
            return m;
        }

        IEnumerator StereoRoutine(float separation)
        {
            Busy = true;
            yield return new WaitForEndOfFrame();
            var cam = GameCamera();
            if (cam != null)
            {
                var rt = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32);
                int frame = Time.frameCount;
                var view = cam.worldToCameraMatrix;
                var pos = cam.transform.position;
                var sides = new[] { -separation * 0.5f, separation * 0.5f };
                var names = new[] { "left", "right" };
                var shots = new Texture2D[2];
                for (int i = 0; i < 2; i++)
                {
                    cam.worldToCameraMatrix = Matrix4x4.TRS(new Vector3(-sides[i], 0f, 0f), Quaternion.identity, Vector3.one) * view;
                    cam.projectionMatrix = EyeProjection(cam, sides[i]);
                    shots[i] = Grab(cam, rt);
                }
                cam.ResetWorldToCameraMatrix();
                cam.ResetProjectionMatrix();
                Plugin.Log.LogInfo(string.Format("STEREO frame {0} same frame after {1} separation {2} cam ({3:F2},{4:F2},{5:F2})", frame, Time.frameCount == frame, separation, pos.x, pos.y, pos.z));
                Diff("left_vs_right", shots[0], shots[1]);
                for (int i = 0; i < 2; i++)
                {
                    Save("stereo_" + names[i], shots[i]);
                    Destroy(shots[i]);
                }
                rt.Release();
                Destroy(rt);
            }
            Busy = false;
        }
    }
}
