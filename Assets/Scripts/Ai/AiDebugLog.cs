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
        // Kept open for the whole session instead of open/append/close per line (see WriteCore) —
        // a single AI turn can log hundreds of lines, and re-opening the file for every one of them
        // was a measured source of main-thread stalls at turn start/end that got worse as the game
        // went on (more armies/heroes -> more lines per turn). AutoFlush still pushes every line to
        // disk immediately (this log exists to survive a crash), it just skips the OS-level
        // open/close overhead of AppendAllText.
        private static StreamWriter _writer;

        // Full candidate/allocation/snapshot diagnostics are useful while tuning one subsystem,
        // but make the normal whole-game trace hard to read. This is the single verbosity owner
        // for both V1 and V2 logging; decision, action, warning and error lines still use Write.
        // Mutable so a debug console/inspector can enable it for a focused run.
        public static bool VerboseEnabled = false;

        // BeforeSceneLoad fires exactly once per game run (Editor Play Mode entry, or a
        // standalone build's own launch), before anything else in the very first scene has had a
        // chance to log — guarantees the file exists and is fresh no matter which scene/object
        // ends up writing to it first.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void BeginSession()
        {
            // Without domain reload (Editor "Enter Play Mode Options"), statics survive across
            // Play sessions in the same process — close whatever the previous session left open
            // first, or re-opening the same path below can fail while the old handle lingers.
            CloseSession();
            try
            {
                // Application.dataPath is "<project>/Assets" in the Editor, "<build>_Data" in a
                // standalone build — one level up is the project root / build folder either way,
                // matching where Unity's own Logs/ already lives (see .gitignore's own [Ll]ogs/).
                string root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                _path = Path.Combine(root, RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? root);
                _writer = new StreamWriter(_path, append: false) { AutoFlush = true };
                _writer.WriteLine($"=== AI debug log — session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                Application.quitting += CloseSession;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"AiDebugLog: couldn't open log file — {e.Message}");
                _path = null;
                _writer = null;
            }
        }

        private static void CloseSession()
        {
            try { _writer?.Dispose(); }
            catch { /* best-effort on shutdown */ }
            _writer = null;
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
            => WriteCore(message, callerFile, callerMember, callerLine);

        // Verbose calls retain the ORIGINAL call-site metadata. Calling Write(message) from here
        // would incorrectly tag every line as AiDebugLog.WriteVerbose.
        public static void WriteVerbose(string message,
            [CallerFilePath] string callerFile = "",
            [CallerMemberName] string callerMember = "",
            [CallerLineNumber] int callerLine = 0)
        {
            if (!VerboseEnabled)
                return;
            WriteCore(message, callerFile, callerMember, callerLine);
        }

        private static void WriteCore(string message, string callerFile,
            string callerMember, int callerLine)
        {
            string source = string.IsNullOrEmpty(callerFile) ? "?" : Path.GetFileNameWithoutExtension(callerFile);
            string tagged = $"[{source}.{callerMember}:{callerLine}] {message}";

            // Same "cosmetic, must not break the real thing" rule as the file write below — and
            // UnityEngine.Debug.Log itself throws when there's no player/editor loaded at all
            // (the Tools/stealth-sim harness runs the game logic headless), which must not abort
            // whatever gameplay path happened to log.
            try { Debug.Log(tagged); }
            catch { /* no Unity log sink available */ }
            if (_writer == null)
                return;
            try
            {
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] {tagged}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"AiDebugLog: write failed, logging to file disabled for the rest of this session — {e.Message}");
                _writer = null;
                _path = null;
            }
        }
    }
}
