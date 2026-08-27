using System;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Game.Ai
{
    // A plain-text trace of every AI decision/action, wide enough to reconstruct a whole AI turn
    // after the fact — separate from Unity's own Console (which is only ever live for as long as
    // the Editor/game stays open, and resets on every domain reload) so the project owner can
    // review it without keeping Play Mode running. One file per run: BeginSession truncates it
    // fresh every time the game actually starts (see the RuntimeInitializeOnLoadMethod below),
    // never appended across separate sessions.
    public static class AiDebugLog
    {
        private const string RelativePath = "Logs/AiDebug.log";
        private static string _path;

        // BeforeSceneLoad fires exactly once per game run (Editor Play Mode entry, or a
        // standalone build's own launch), before anything else in the very first scene has had a
        // chance to log — guarantees the file exists and is fresh no matter which scene/object
        // ends up writing to it first.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void BeginSession()
        {
            try
            {
                // Application.dataPath is "<project>/Assets" in the Editor, "<build>_Data" in a
                // standalone build — one level up is the project root / build folder either way,
                // matching where Unity's own Logs/ already lives (see .gitignore's own [Ll]ogs/).
                string root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                _path = Path.Combine(root, RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? root);
                File.WriteAllText(_path, $"=== AI debug log — session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"AiDebugLog: couldn't open log file — {e.Message}");
                _path = null;
            }
        }

        // Still shows up live in the Console (same as every call site used before this existed),
        // plus appended to the file. A write failure here must never take down the AI turn that
        // called it — same "cosmetic, must not break the real thing" reasoning as
        // HexSelectionController.Movement.cs's own path-arrow try/catch — so this only warns
        // once, then quietly stops trying for the rest of the session.
        //
        // The Caller* params are filled in by the COMPILER at the call site, not passed by hand —
        // every Write(...) call automatically tags itself with which script/method/line actually
        // logged it, per the project owner's own ask ("должен быть виден вызывающий скрипт"), so
        // tracing a log line back to the exact source is a straight jump instead of a text search.
        public static void Write(string message,
            [CallerFilePath] string callerFile = "",
            [CallerMemberName] string callerMember = "",
            [CallerLineNumber] int callerLine = 0)
        {
            string source = string.IsNullOrEmpty(callerFile) ? "?" : Path.GetFileNameWithoutExtension(callerFile);
            string tagged = $"[{source}.{callerMember}:{callerLine}] {message}";

            // Same "cosmetic, must not break the real thing" rule as the file write below — and
            // UnityEngine.Debug.Log itself throws when there's no player/editor loaded at all
            // (the Tools/stealth-sim harness runs the game logic headless), which must not abort
            // whatever gameplay path happened to log.
            try { Debug.Log(tagged); }
            catch { /* no Unity log sink available */ }
            if (_path == null)
                return;
            try
            {
                File.AppendAllText(_path, $"[{DateTime.Now:HH:mm:ss}] {tagged}{Environment.NewLine}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"AiDebugLog: write failed, logging to file disabled for the rest of this session — {e.Message}");
                _path = null;
            }
        }
    }
}
