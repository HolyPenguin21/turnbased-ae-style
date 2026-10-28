// Compile-only declarations for cached-reference binding of native EditMode fixtures.
// These APIs are supplied by UnityEditor/Test Framework in the real editor. Their bodies
// deliberately throw: referencing this adapter cannot execute or simulate native checks.
using System;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityEditor.SceneManagement
{
    public enum NewSceneSetup { EmptyScene, DefaultGameObjects }
    public enum NewSceneMode { Single, Additive }
    public static class EditorSceneManager
    {
        public static Scene NewScene(NewSceneSetup setup, NewSceneMode mode) =>
            throw new NotSupportedException("Native EditMode scene creation requires Unity Editor.");
        public static bool CloseScene(Scene scene, bool removeScene) =>
            throw new NotSupportedException("Native EditMode scene cleanup requires Unity Editor.");
    }
}
namespace UnityEngine.TestTools
{
    public static class LogAssert
    {
        public static void Expect(LogType type, Regex message) =>
            throw new NotSupportedException("Native log expectations require Unity Test Framework.");
    }
}
