using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Game.Ai
{
    [Flags]
    public enum AiVerboseArea
    {
        None = 0,
        Recon = 1 << 0,
        Aviation = 1 << 1,
        Reservations = 1 << 2,
        Correlation = 1 << 3,
        All = Recon | Aviation | Reservations | Correlation,
    }

    // A plain-text trace of every AI decision/action, wide enough to reconstruct a whole AI turn
    // after the fact — separate from Unity's own Console (which is only ever live for as long as
    // the Editor/game stays open, and resets on every domain reload) so the project owner can
    // review it without keeping Play Mode running. One file per run: BeginSession truncates it
    // fresh every time the game actually starts (see the RuntimeInitializeOnLoadMethod below),
    // never appended across separate sessions.
    public static class AiDebugLog
    {
        private const string RelativePath = "Logs/AiDebug.log";
        // Kept open for the whole session instead of open/append/close per line (see WriteCore) —
        // a single AI turn can log hundreds of lines, and re-opening the file for every one of them
        // was a measured source of main-thread stalls at turn start/end that got worse as the game
        // went on (more armies/heroes -> more lines per turn). AutoFlush still pushes every line to
        // disk immediately (this log exists to survive a crash), it just skips the OS-level
        // open/close overhead of AppendAllText.
        private static StreamWriter _writer;

        // WriteDeduped support — V2's "recompute everything each cycle" architecture (see
        // AiStrategyV2Pipeline.cs header) re-derives the same objectives/commitments/missions many
        // times per turn, and most re-derivations conclude exactly what the previous one did. Call
        // sites that log a per-item, per-cycle status line (an objective ACCEPT, a commitment CLAIM,
        // an allocator pool dump, ...) use WriteDeduped instead of Write so an unchanged line prints
        // once per turn scope (see ResetDedupScope) and only re-prints when the text actually
        // differs — the raw trace still reflects every real change, it just stops repeating the
        // same fact cycle after cycle.
        private static readonly Dictionary<string, string> _dedupLastByKey = new Dictionary<string, string>();
        // WriteDedupedWithId support — id-free content already printed this scope -> the trace id
        // of the line that printed it in full.
        private static readonly Dictionary<string, string> _firstIdByContent = new Dictionary<string, string>();

        // The file (Logs/AiDebug.log) is the actual trace anyone reads back after a run — the
        // Editor Console mirror was only ever a live convenience, and Debug.Log itself (console
        // entry formatting, stack trace capture, window repaint) is real per-call overhead that
        // a heavy AI turn pays hundreds of times over for a window nobody's watching. Off by
        // default; flip on for a session where the live Console view is actually wanted.
        public static bool LogToUnityConsole = false;

        // Compact is the default trace. Verbose keeps expensive/repetitive diagnostics available
        // for focused investigations without paying their I/O/readability cost on every QA run.
        public static bool Verbose = false;
        public static AiVerboseArea VerboseAreas = AiVerboseArea.None;

        public static bool IsVerbose(AiVerboseArea area) =>
            Verbose || (VerboseAreas & area) != 0;

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
            ResetDedupScope();
            try
            {
                // Application.dataPath is "<project>/Assets" in the Editor, "<build>_Data" in a
                // standalone build — one level up is the project root / build folder either way,
                // matching where Unity's own Logs/ already lives (see .gitignore's own [Ll]ogs/).
                string root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                string path = Path.Combine(root, RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? root);
                _writer = new StreamWriter(path, append: false) { AutoFlush = true };
                _writer.WriteLine($"=== AI debug log — session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                Application.quitting -= CloseSession;
                Application.quitting += CloseSession;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"AiDebugLog: couldn't open log file — {e.Message}");
                _writer = null;
            }
        }

        private static void CloseSession()
        {
            try { _writer?.Dispose(); }
            catch { /* best-effort on shutdown */ }
            _writer = null;
        }

        // Appended to the file; also mirrored live to the Console when LogToUnityConsole is on
        // (off by default — see that field's own comment). A write failure here must never take
        // down the AI turn that called it — same "cosmetic, must not break the real thing" reasoning as
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

        public static void WriteVerbose(string message,
            [CallerFilePath] string callerFile = "",
            [CallerMemberName] string callerMember = "",
            [CallerLineNumber] int callerLine = 0)
        {
            if (!Verbose) return;
            WriteCore(message, callerFile, callerMember, callerLine);
        }

        // Suppress an unchanged recomputation from the same call site, while still emitting the
        // line again when its actual decision/details change later in the same turn scope.
        public static void WriteRepeatSuppressed(string message,
            [CallerFilePath] string callerFile = "",
            [CallerMemberName] string callerMember = "",
            [CallerLineNumber] int callerLine = 0)
        {
            string fullKey = $"{callerFile}:{callerLine}|repeat";
            if (_dedupLastByKey.TryGetValue(fullKey, out string last) && last == message)
                return;
            _dedupLastByKey[fullKey] = message;
            WriteCore(message, callerFile, callerMember, callerLine);
        }

        // Compare a whole deterministic diagnostic block as one state. If any line changes the
        // block is emitted again in full; an unchanged recomputation produces no output.
        public static void WriteBlockRepeatSuppressed(string dedupKey, IEnumerable<string> messages,
            [CallerFilePath] string callerFile = "",
            [CallerMemberName] string callerMember = "",
            [CallerLineNumber] int callerLine = 0)
        {
            if (messages == null) return;
            var block = new List<string>();
            foreach (string message in messages)
                if (!string.IsNullOrEmpty(message)) block.Add(message);
            string fingerprint = string.Join("\n", block);
            string fullKey = $"{callerFile}:{callerLine}|block|{dedupKey}";
            if (_dedupLastByKey.TryGetValue(fullKey, out string last) && last == fingerprint)
                return;
            _dedupLastByKey[fullKey] = fingerprint;
            foreach (string message in block)
                WriteCore(message, callerFile, callerMember, callerLine);
        }

        // dedupKey identifies WHICH recurring thing this line is about (e.g. a target id, an actor
        // id, an allocator pass name) — distinct keys at the same call site are tracked and printed
        // independently. Only a byte-for-byte-identical repeat of the previous message for that key
        // is suppressed, so any real change (a number moving, a decision flipping) still prints.
        public static void WriteDeduped(string dedupKey, string message,
            [CallerFilePath] string callerFile = "",
            [CallerMemberName] string callerMember = "",
            [CallerLineNumber] int callerLine = 0)
        {
            string fullKey = $"{callerFile}:{callerLine}|{dedupKey}";
            if (_dedupLastByKey.TryGetValue(fullKey, out string last) && last == message)
                return;
            _dedupLastByKey[fullKey] = message;
            WriteCore(message, callerFile, callerMember, callerLine);
        }

        // For a line that carries a per-pass correlation id (e.g. a demand's "[T4-P5-M-D39]"): the
        // id changes every pass even when nothing else does, so ordinary dedup cannot match it.
        // Compact mode emits the full content once per turn scope and suppresses identical
        // recomputations. Correlation-verbose mode additionally prints a short alias mapping each
        // fresh id to the first full line, preserving forensic grepability when explicitly needed.
        public static void WriteDedupedWithId(string id, string message,
            [CallerFilePath] string callerFile = "",
            [CallerMemberName] string callerMember = "",
            [CallerLineNumber] int callerLine = 0)
        {
            if (string.IsNullOrEmpty(id))
            {
                WriteCore(message, callerFile, callerMember, callerLine);
                return;
            }
            string contentKey = $"{callerFile}:{callerLine}|{message.Replace(id, "#")}";
            if (_firstIdByContent.TryGetValue(contentKey, out string firstId))
            {
                if (IsVerbose(AiVerboseArea.Correlation))
                    WriteCore($"[AI][V2]   {id} = {firstId} (same line as earlier this turn)",
                        callerFile, callerMember, callerLine);
                return;
            }
            _firstIdByContent[contentKey] = id;
            WriteCore(message, callerFile, callerMember, callerLine);
        }

        // Dedup memory is scoped to one player's turn (called from AiV2Trace.BeginMain), so every
        // turn's log is readable on its own and one player's line never suppresses another's.
        public static void ResetDedupScope()
        {
            _dedupLastByKey.Clear();
            _firstIdByContent.Clear();
        }

        private static void WriteCore(string message, string callerFile,
            string callerMember, int callerLine)
        {
            using var __profile = new Game.Core.ProfileScope("AI/DebugLog.Write");
            string source = string.IsNullOrEmpty(callerFile) ? "?" : Path.GetFileNameWithoutExtension(callerFile);
            string tagged = $"[{source}.{callerMember}:{callerLine}] {message}";

            // Same "cosmetic, must not break the real thing" rule as the file write below — and
            // UnityEngine.Debug.Log itself throws when there's no player/editor loaded at all
            // (the Tools/stealth-sim harness runs the game logic headless), which must not abort
            // whatever gameplay path happened to log.
            if (LogToUnityConsole)
            {
                try { Debug.Log(tagged); }
                catch { /* no Unity log sink available */ }
            }
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
            }
        }
    }
}
