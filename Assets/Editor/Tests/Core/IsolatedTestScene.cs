#if UNITY_INCLUDE_TESTS
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Game.EditorTests
{
    // An empty scene a fixture owns for one test. Unity refuses an additive new scene while the
    // only open scene is untitled (the batch-mode test runner's default), so then the empty scene
    // replaces it, and Close leaves a fresh empty scene instead of closing the last one.
    internal static class IsolatedTestScene
    {
        internal static Scene Open()
        {
            bool onlyUntitled = true;
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (!string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path)) onlyUntitled = false;
            return EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                onlyUntitled ? NewSceneMode.Single : NewSceneMode.Additive);
        }

        internal static void Close(Scene scene)
        {
            if (!scene.IsValid()) return;
            if (SceneManager.sceneCount > 1) EditorSceneManager.CloseScene(scene, true);
            else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
    }
}
#endif
