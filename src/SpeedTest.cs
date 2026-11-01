using System.Collections;
using UnityEngine;

namespace OriVR
{
    // Lab test: does the game keep its real-world speed when the tick rate changes? Sets a frame rate and tick length,
    // then logs ticks, game time and Ori's top speed, all against real time.
    internal class SpeedTest : MonoBehaviour
    {
        // fixedHz 0 leaves the tick length alone.
        internal void SetRate(int fps, int fixedHz)
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = fps;
            if (fixedHz > 0)
            {
                Time.fixedDeltaTime = 1f / fixedHz;
                Time.maximumDeltaTime = 1f / fixedHz;
            }
            Plugin.Log.LogInfo(string.Format("RATE target {0} fps, fixedDelta {1:F5}, maxDelta {2:F5}", fps, Time.fixedDeltaTime, Time.maximumDeltaTime));
        }

        internal void Run(float seconds)
        {
            StartCoroutine(Measure(seconds));
        }

        IEnumerator Measure(float seconds)
        {
            float start = Time.realtimeSinceStartup, gameStart = Time.time;
            int frames = Time.frameCount, ticks = Plugin.Ticks;
            float lastReal = start, lastX = OriX(), lastY = OriY();
            float top = 0f, topUp = 0f, topDown = 0f;
            float lowY = lastY, highY = lastY;
            float travelled = 0f;
            while (Time.realtimeSinceStartup - start < seconds)
            {
                yield return null;
                float now = Time.realtimeSinceStartup;
                float x = OriX(), y = OriY();
                if (y < lowY) lowY = y;
                if (y > highY) highY = y;
                if (now - lastReal < 0.1f) continue;
                float vx = Mathf.Abs(x - lastX) / (now - lastReal), vy = (y - lastY) / (now - lastReal);
                if (vx > top) top = vx;
                if (vy > topUp) topUp = vy;
                if (-vy > topDown) topDown = -vy;
                travelled += Mathf.Abs(x - lastX);
                lastX = x; lastY = y; lastReal = now;
            }
            float real = Time.realtimeSinceStartup - start;
            Plugin.Log.LogInfo(string.Format("SPEED real {0:F2} s, {1:F1} fps, {2:F1} ticks/s, game time per real second {3:F3}, Ori top run {4:F2} u/s, top rise {5:F2}, top fall {6:F2}, travelled {7:F1}, height range {8:F2}",
                real, (Time.frameCount - frames) / real, (Plugin.Ticks - ticks) / real, (Time.time - gameStart) / real, top, topUp, topDown, travelled, highY - lowY));
        }

        static float OriX() { return Game.Characters.Current != null ? Game.Characters.Current.Position.x : 0f; }
        static float OriY() { return Game.Characters.Current != null ? Game.Characters.Current.Position.y : 0f; }
    }
}
