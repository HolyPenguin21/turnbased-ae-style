using System;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Game.Combat
{
    // A plain-text trace of every AI decision/action made during a Tactical Battle Module fight
    // (arrangement, retreat assessment, per-unit move/attack choice, dice rolls, Fate-spend
    // decisions) — the battle-side twin of Game.Ai.AiDebugLog (see that file's own comment for
    // the full reasoning: separate from Unity's own Console so the project owner can review a
    // fight after the fact without keeping Play Mode running). Deliberately its own file/class
    // rather than reusing AiDebugLog directly — a map-AI turn and a battle can both be in flight
    // narratively close together, and the project owner asked for "such a [file] for the battle
    // too" (a dedicated log alongside the existing map one), not one merged stream to pick apart.
    // One file per run: BeginSession truncates it fresh every time the game actually starts, same
    // as AiDebugLog's own BeforeSceneLoad hook.
    public static class BattleDebugLog
    {
        private const string RelativePath = "Logs/BattleDebug.log";
        private static string _path;
        // Per-SESSION battle sequence number (see BeginBattle) — every battle fought this
        // session gets the next number and stays in this one file rather than each battle
        // getting/truncating its own; only BeginSession (below) ever resets this, so a hex that
        // sees three fights in one sitting shows up as three separate, still-readable #N blocks
        // in the same log instead of the third overwriting the first two.
        private static int _battleCounter;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void BeginSession()
        {
            _battleCounter = 0;
            try
            {
                string root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                _path = Path.Combine(root, RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? root);
                File.WriteAllText(_path, $"=== Battle debug log — session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"BattleDebugLog: couldn't open log file — {e.Message}");
                _path = null;
            }
        }

        // One header line per battle fought this session (see BattleScreenUI.Show, the one
        // entry point for a fresh Tactical Battle Module encounter) — per the project owner's
        // own spec: "порядковый номер сражения, название армии - название армии - хекс". Every
        // Write()/BeginRound() call that follows (until the next BeginBattle) is understood to
        // belong to this same battle — plain chronological order in one append-only file, not a
        // separate file/section per battle.
        public static void BeginBattle(string attackerArmyName, string defenderArmyName, string hexLabel)
        {
            _battleCounter++;
            string header = $"{Environment.NewLine}=== Battle #{_battleCounter}: {attackerArmyName} - {defenderArmyName} - {hexLabel} ==={Environment.NewLine}";
            AppendRaw(header);
        }

        // One marker line per round within the CURRENT battle (see BattleScreenUI.BeginRound) —
        // the "потом логи по ходам этого сражения" half of the same spec: every decision logged
        // between this and the next BeginRound/BeginBattle happened during this round.
        public static void BeginRound(int round)
        {
            AppendRaw($"--- Round {round} ---{Environment.NewLine}");
        }

        // Shared by BeginBattle/BeginRound — a bare structural line (no caller tag, no per-line
        // timestamp prefix; the header/marker text itself already says what it is) that still
        // goes through the same live-Console + file-append + write-failure-disables-itself path
        // as Write() below.
        private static void AppendRaw(string line)
        {
            Debug.Log(line);
            if (_path == null)
                return;
            try
            {
                File.AppendAllText(_path, line);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"BattleDebugLog: write failed, logging to file disabled for the rest of this session — {e.Message}");
                _path = null;
            }
        }

        // Same CallerFilePath/CallerMemberName/CallerLineNumber auto-tagging as AiDebugLog.Write
        // — every call site tags itself with which script/method/line logged it, so tracing a
        // line back to the exact source is a straight jump instead of a text search.
        public static void Write(string message,
            [CallerFilePath] string callerFile = "",
            [CallerMemberName] string callerMember = "",
            [CallerLineNumber] int callerLine = 0)
        {
            string source = string.IsNullOrEmpty(callerFile) ? "?" : Path.GetFileNameWithoutExtension(callerFile);
            string tagged = $"[{source}.{callerMember}:{callerLine}] {message}";

            Debug.Log(tagged);
            if (_path == null)
                return;
            try
            {
                File.AppendAllText(_path, $"[{DateTime.Now:HH:mm:ss}] {tagged}{Environment.NewLine}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"BattleDebugLog: write failed, logging to file disabled for the rest of this session — {e.Message}");
                _path = null;
            }
        }

        // Renders a dice-roll result as a compact hit/miss string (e.g. "X.XX." for hit/hit/miss/
        // hit/miss) — shared by every roll/reroll log line below instead of each site re-deriving
        // its own formatting, so the same array always reads the same way in the log.
        public static string DiceString(bool[] dice)
        {
            if (dice == null)
                return "(none)";
            if (dice.Length == 0)
                return "(0 dice)";
            var chars = new char[dice.Length];
            for (int i = 0; i < dice.Length; i++)
                chars[i] = dice[i] ? 'X' : '.';
            return new string(chars);
        }
    }
}
