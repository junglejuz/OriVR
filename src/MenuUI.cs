using System;
using CatlikeCoding.TextBox;
using SmartInput;
using UnityEngine;

namespace OriVR
{
    // The in-headset settings menu. Click both sticks to open or close it. It is drawn with the game's own text system
    // on the HUD layer, so both eyes see it at stage distance. The game is paused while it is open.
    internal class MenuUI : MonoBehaviour
    {
        const int HudLayer = 8;
        static readonly Color Gold = new Color(1f, 0.82f, 0.45f, 1f);
        static readonly Color Dim = new Color(0.75f, 0.78f, 0.8f, 1f);
        static readonly Color Title = new Color(0.72f, 0.9f, 1f, 1f);
        const int Rows = 10;  // distance, size, depth, pop-out, sharpen, resolution, picture, recenter, defaults, close

        internal XrRig Rig;
        internal Action<int, int> Adjust;  // row, direction
        internal Action<int> Activate;     // row
        internal Func<int, string> RowText;
        internal Action Closed;

        internal bool IsOpen { get; private set; }

        GameObject root;
        TextBox titleBox, hintBox;
        Transform bar, line;
        TextBox[] rows = new TextBox[Rows];
        string[] shown = new string[Rows];
        int selected, lastSelected = -1;
        bool bothWas, waitRelease;
        float savedScale = 1f;
        float nextRepeat;
        int heldX, heldY;
        bool aWas, bWas;

        internal void Open()
        {
            if (IsOpen || waitRelease) return;
            if (root == null && !Build()) return;
            root.SetActive(true);
            IsOpen = true;
            selected = 0;
            lastSelected = -1;
            for (int i = 0; i < Rows; i++) shown[i] = null;
            savedScale = Time.timeScale;
            Time.timeScale = 0f;
            aWas = bWas = true;  // ignore presses already held
            Refresh();
            Plugin.Log.LogInfo("MENU open");
        }

        internal void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            if (root != null) root.SetActive(false);
            waitRelease = true;  // the game stays paused until buttons are up, so it does not see them
            Plugin.Log.LogInfo("MENU close");
            if (Closed != null) Closed();
        }

        bool Build()
        {
            TextStyleCollection style = null;
            foreach (var box in Resources.FindObjectsOfTypeAll<MessageBox>())
            {
                if (box.TextBox != null && box.TextBox.styleCollection != null)
                {
                    style = box.TextBox.styleCollection;
                    break;
                }
            }
            if (style == null)
            {
                var all = Resources.FindObjectsOfTypeAll<TextStyleCollection>();
                if (all.Length > 0) style = all[0];
            }
            if (style == null)
            {
                Plugin.Log.LogWarning("menu: no text style found yet");
                return false;
            }
            Plugin.Log.LogInfo("menu text style: " + style.name);

            root = new GameObject("OriVR menu");
            DontDestroyOnLoad(root);
            root.transform.position = new Vector3(0f, 0f, -3f);  // in front of the game's own HUD
            MakePanel(root.transform);
            titleBox = MakeText(root.transform, style, 2.72f, 0.95f);
            titleBox.color = Title;
            Show(titleBox, "VR SETTINGS");
            for (int i = 0; i < Rows; i++)
            {
                rows[i] = MakeText(root.transform, style, RowY(i), 0.66f);
            }
            hintBox = MakeText(root.transform, style, -2.72f, 0.5f);
            hintBox.color = Dim;
            Show(hintBox, "Up and down: choose.   Left and right: change.   A: select.   B: close");
            SetLayer(root);
            root.SetActive(false);
            return true;
        }

        TextBox MakeText(Transform parent, TextStyleCollection style, float y, float size)
        {
            var go = new GameObject("text");
            go.layer = HudLayer;
            go.transform.parent = parent;
            go.transform.localPosition = new Vector3(0f, y, 0f);
            var box = go.AddComponent<TextBox>();
            box.alignment = AlignmentMode.Center;
            box.horizontalAnchor = HorizontalAnchorMode.Center;
            box.verticalAnchor = VerticalAnchorMode.Middle;
            box.width = 12f;
            box.maxHeight = 2f;
            box.size = size;
            box.color = Color.white;
            box.SetStyleCollection(style);
            return box;
        }

        static void Show(TextBox box, string text)
        {
            box.SetText(text);
            box.RenderText();
            SetLayer(box.gameObject);
        }

        static void SetLayer(GameObject go)
        {
            go.layer = HudLayer;
            foreach (Transform t in go.transform) SetLayer(t.gameObject);
        }

        // A dark panel with soft edges, and a blue glow bar for the selected row, in the style of the game's pause menu.
        void MakePanel(Transform parent)
        {
            Shader shader = null;
            string[] names = { "Sprites/Default", "Unlit/Transparent", "UI/Default", "GUI/Text Shader", "Hidden/Internal-GUITexture" };
            foreach (var n in names)
            {
                shader = Shader.Find(n);
                if (shader != null) { Plugin.Log.LogInfo("menu panel shader: " + n); break; }
            }
            if (shader == null) { Plugin.Log.LogWarning("menu: no panel shader, text only"); return; }
            MakeQuad(parent, shader, "panel", 5.6f, 3.7f, 0.5f, SoftBox(128, 128, new Color(0.015f, 0.04f, 0.075f), 0.97f, 0.12f, 0.12f));
            bar = MakeQuad(parent, shader, "bar", 4.2f, 0.27f, 0.25f, SoftBox(128, 16, new Color(0.2f, 0.5f, 0.85f), 0.55f, 0.5f, 0.5f));
            line = MakeQuad(parent, shader, "line", 3.2f, 0.018f, 0.25f, SoftBox(128, 4, new Color(0.45f, 0.75f, 1f), 0.8f, 0.5f, 0f));
            line.localPosition = new Vector3(0f, 2.25f, 0.25f);
        }

        static Transform MakeQuad(Transform parent, Shader shader, string name, float w, float h, float z, Texture2D tex)
        {
            var go = new GameObject(name);
            go.layer = HudLayer;
            go.transform.parent = parent;
            go.transform.localPosition = new Vector3(0f, 0f, z);
            var mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(-w, -h, 0f), new Vector3(-w, h, 0f), new Vector3(w, h, 0f), new Vector3(w, -h, 0f) };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
            mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mat = new Material(shader);
            mat.mainTexture = tex;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go.transform;
        }

        // A texture that fades to clear at its edges. fadeX and fadeY are the fraction of each half used for the fade.
        static Texture2D SoftBox(int w, int h, Color color, float alpha, float fadeX, float fadeY)
        {
            var tex = new Texture2D(w, h, TextureFormat.ARGB32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float ex = 1f - Mathf.Abs((x + 0.5f) / w * 2f - 1f);
                    float ey = 1f - Mathf.Abs((y + 0.5f) / h * 2f - 1f);
                    float ax = fadeX > 0f ? Mathf.SmoothStep(0f, 1f, ex / fadeX) : 1f;
                    float ay = fadeY > 0f ? Mathf.SmoothStep(0f, 1f, ey / fadeY) : 1f;
                    tex.SetPixel(x, y, new Color(color.r, color.g, color.b, alpha * ax * ay));
                }
            }
            tex.Apply();
            return tex;
        }

        internal void Refresh()
        {
            if (!IsOpen) return;
            for (int i = 0; i < Rows; i++)
            {
                var text = RowText(i);
                if (text == shown[i] && lastSelected == selected) continue;
                shown[i] = text;
                rows[i].color = i == selected ? Gold : Dim;
                Show(rows[i], text);
            }
            if (bar != null) bar.localPosition = new Vector3(0f, RowY(selected), 0.25f);
            lastSelected = selected;
        }

        static float RowY(int row) { return 1.8f - row * 0.43f; }

        void Update()
        {
            if (XboxLiveController.Instance == null) return;  // the game's pad system does not exist yet during boot
            bool both = XboxControllerInput.GetButton(XboxControllerInput.Button.LeftStick)
                     && XboxControllerInput.GetButton(XboxControllerInput.Button.RightStick);
            if (both && !bothWas)
            {
                if (IsOpen) Close();
                else Open();
            }
            bothWas = both;

            if (waitRelease)
            {
                bool anyHeld = both || XboxControllerInput.GetButton(XboxControllerInput.Button.ButtonA)
                                    || XboxControllerInput.GetButton(XboxControllerInput.Button.ButtonB)
                                    || XboxControllerInput.GetButton(XboxControllerInput.Button.LeftStick)
                                    || XboxControllerInput.GetButton(XboxControllerInput.Button.RightStick);
                if (!anyHeld)
                {
                    waitRelease = false;
                    Time.timeScale = savedScale;
                }
                return;
            }
            if (!IsOpen) return;

            bool a = XboxControllerInput.GetButton(XboxControllerInput.Button.ButtonA);
            bool b = XboxControllerInput.GetButton(XboxControllerInput.Button.ButtonB);
            if (b && !bWas) { bWas = true; aWas = a; Close(); return; }
            if (a && !aWas) Activate(selected);
            aWas = a;
            bWas = b;

            int y = Direction(XboxControllerInput.Axis.LeftStickY, XboxControllerInput.Axis.DpadY);
            int x = Direction(XboxControllerInput.Axis.LeftStickX, XboxControllerInput.Axis.DpadX);
            float now = Time.realtimeSinceStartup;
            if (y != heldY || x != heldX)
            {
                nextRepeat = now + 0.35f;
                Step(x, y);
            }
            else if ((x != 0 || y != 0) && now >= nextRepeat)
            {
                nextRepeat = now + 0.14f;
                Step(x, y);
            }
            heldX = x;
            heldY = y;
        }

        static int Direction(XboxControllerInput.Axis stick, XboxControllerInput.Axis dpad)
        {
            float v = XboxControllerInput.GetAxis(stick);
            if (Mathf.Abs(v) < 0.5f) v = XboxControllerInput.GetAxis(dpad);
            return v > 0.5f ? 1 : (v < -0.5f ? -1 : 0);
        }

        void Step(int x, int y)
        {
            if (y != 0) selected = (selected - y + Rows) % Rows;
            else if (x != 0) Adjust(selected, x);
            else return;
            Refresh();
        }
    }
}
