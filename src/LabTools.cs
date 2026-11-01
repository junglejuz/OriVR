using System.Collections;
using System.Text;
using Core;
using Game;
using UnityEngine;

namespace OriVR
{
    // Lab only, started from the command file: move to any scene and start a cross fade, so effects can be tested.
    internal class LabTools : MonoBehaviour
    {
        internal void ListScenes(string word)
        {
            if (Scenes.Manager == null) return;
            var sb = new StringBuilder("SCENES:");
            foreach (var scene in Scenes.Manager.AllScenes)
            {
                if (scene.Scene.ToLowerInvariant().Contains(word.ToLowerInvariant())) sb.Append(' ').Append(scene.Scene);
            }
            Plugin.Log.LogInfo(sb.ToString());
        }

        internal void GoTo(string sceneName)
        {
            StartCoroutine(GoToRoutine(sceneName));
        }

        // The same steps as the game's own debug menu (DebugMenuB.GoToScene).
        IEnumerator GoToRoutine(string sceneName)
        {
            var info = Scenes.Manager.GetSceneInformation(sceneName);
            if (info == null)
            {
                Plugin.Log.LogWarning("goto: no scene " + sceneName);
                yield break;
            }
            Scenes.Manager.AutoLoadingUnloading = false;
            Scenes.Manager.UnloadAllScenes();
            Scenes.Manager.DestroyManager.DestroyAll();
            SuspensionManager.SuspendAll();
            while (Scenes.Manager.ResourcesNeedUnloading) yield return new WaitForFixedUpdate();
            SuspensionManager.ResumeAll();
            GameController.Instance.ResetStateForDebugMenuGoToScene();
            GoToSceneController.Instance.GoToScene(info, null, false);
            Plugin.Log.LogInfo("GOTO " + sceneName);
        }

        internal void CrossFade(float seconds)
        {
            var character = Characters.Current;
            var root = character != null ? Scenes.Manager.FindLoadedSceneRootFromPosition(character.Position) : null;
            if (root == null || root.MetaData == null)
            {
                Plugin.Log.LogWarning("crossfade: no scene at Ori's position");
                return;
            }
            UI.Cameras.System.CrossFadeManager.PerformCrossFade(root.MetaData, seconds);
            Plugin.Log.LogInfo("CROSSFADE to " + root.MetaData.name + " over " + seconds + " s");
        }
    }
}
