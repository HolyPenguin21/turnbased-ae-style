using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Game.Economy;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai
{
    // Match-level metrics for AI tuning: one number per question instead of reading AiDebug.log
    // by hand. Two files, next to AiDebug.log:
    //   Logs/AiMatchTurns.csv   — one row per player per round (stock, armies, units, buildings),
    //                             for charting a match or diffing two runs.
    //   Logs/AiMatchSummary.txt — per-player totals. Rewritten at every round start and once more
    //                             on game over / quitting Play Mode, so a match stopped half-way
    //                             still leaves a complete summary of what was played.
    // Purely observational: every Record* call is fed by the one authoritative place the event
    // already happens (see each caller), and nothing here is ever read back by the AI itself.
    // Same "cosmetic, must never break the real thing" rule as AiDebugLog — all IO is guarded.
    public static class AiMatchStats
    {
        private const string TurnsRelativePath = "Logs/AiMatchTurns.csv";
        private const string SummaryRelativePath = "Logs/AiMatchSummary.txt";

        private sealed class PlayerStats
        {
            public PlayerSetupData Player;
            public int AiTurns;
            public long ApStart, ApSpent, ApEnd;
            public int StrandedTurns;              // ended with >= initiativeWasteLeftoverFrac of start AP
            public int DiceBought;
            public readonly int[] DiceSpent = new int[4];
            public int InitiativeSuppressed;
            public int WinVsPlayer, LossVsPlayer, DrawVsPlayer;
            public int WinVsNeutral, LossVsNeutral, DrawVsNeutral;
            public int Retreats, RetreatDestroyed;
            public int BasesBuilt, FacilitiesBuilt;
            public int BasesCaptured, BasesLost, FacilitiesDestroyed, FacilitiesLost;
            public int PeakBases, PeakFieldArmies, PeakUnits;
            public int EliminatedOnTurn;
            public Snapshot Last;
        }

        private struct Snapshot
        {
            public int[] Res;
            public int FieldArmies, Units, Bases, Facilities;
        }

        private static readonly ResourceType[] Types =
            { ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech };

        private static readonly Dictionary<PlayerSetupData, PlayerStats> ByPlayer =
            new Dictionary<PlayerSetupData, PlayerStats>();
        private static readonly List<PlayerSetupData> Order = new List<PlayerSetupData>();
        private static bool _active;
        private static bool _hooked;
        private static int _turn;
        private static string _outcome;
        private static DateTime _startedAt;
        private static StreamWriter _turnsWriter;
        private static string _summaryPath;

        // GameTurnController.BeginGame — every citadel is placed, nothing has been played yet.
        public static void BeginMatch(IEnumerable<PlayerSetupData> players)
        {
            CloseWriter();
            ByPlayer.Clear();
            Order.Clear();
            _turn = 0;
            _outcome = null;
            _startedAt = DateTime.Now;
            foreach (PlayerSetupData p in players ?? Enumerable.Empty<PlayerSetupData>())
            {
                if (p == null || p.IsNeutral || ByPlayer.ContainsKey(p))
                    continue;
                ByPlayer[p] = new PlayerStats { Player = p };
                Order.Add(p);
            }
            _active = true;
            if (!_hooked)
            {
                Application.quitting += () => Finish("stopped (Play Mode exit / quit)");
                _hooked = true;
            }
            try
            {
                string root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                string turnsPath = Path.Combine(root, TurnsRelativePath);
                _summaryPath = Path.Combine(root, SummaryRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(turnsPath) ?? root);
                _turnsWriter = new StreamWriter(turnsPath, append: false) { AutoFlush = true };
                _turnsWriter.WriteLine("turn,player,kind,H,E,M,T,fieldArmies,units,bases,facilities,eliminated");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"AiMatchStats: couldn't open output files — {e.Message}");
                _turnsWriter = null;
                _summaryPath = null;
            }
        }

        // GameTurnController.ProceedWithNewTurn, right after TurnNumber advances — before this
        // round's income, so each row is the stock the previous round actually left behind.
        public static void OnRoundStarted(int turn)
        {
            if (!_active)
                return;
            _turn = turn;
            foreach (PlayerSetupData p in Order)
            {
                PlayerStats s = ByPlayer[p];
                Snapshot snap = Capture(p);
                s.Last = snap;
                s.PeakBases = Mathf.Max(s.PeakBases, snap.Bases);
                s.PeakFieldArmies = Mathf.Max(s.PeakFieldArmies, snap.FieldArmies);
                s.PeakUnits = Mathf.Max(s.PeakUnits, snap.Units);
                WriteTurnRow($"{turn},{Csv(p.Nickname)},{Kind(p)},{snap.Res[0]},{snap.Res[1]},{snap.Res[2]},{snap.Res[3]},"
                    + $"{snap.FieldArmies},{snap.Units},{snap.Bases},{snap.Facilities},{(p.IsEliminated ? 1 : 0)}");
            }
            WriteSummary();
        }

        // AiStrategyV2Pipeline.RecordInitiativeAnalytics — the same end-of-turn AP telemetry the
        // initiative module already keeps.
        public static void RecordAiTurn(PlayerSetupData player, int startAp, int apSpent, int endAp)
        {
            if (!TryGet(player, out PlayerStats s))
                return;
            s.AiTurns++;
            s.ApStart += startAp;
            s.ApSpent += apSpent;
            s.ApEnd += endAp;
            if (startAp > 0 && (float)endAp / startAp >= V2.AiConfigV2.initiativeWasteLeftoverFrac)
                s.StrandedTurns++;
        }

        // InitiativeCoordinatorV2 — dice actually applied, and the H/E/M/T units that paid them.
        public static void RecordInitiativeBought(PlayerSetupData player, int dice, int[] spent)
        {
            if (!TryGet(player, out PlayerStats s) || dice <= 0)
                return;
            s.DiceBought += dice;
            for (int i = 0; i < 4 && spent != null && i < spent.Length; i++)
                s.DiceSpent[i] += spent[i];
        }

        public static void RecordInitiativeSuppressed(PlayerSetupData player)
        {
            if (TryGet(player, out PlayerStats s))
                s.InitiativeSuppressed++;
        }

        // BattleScreenUI.FinishBattleEnd — a fight resolved to the end (no retreat).
        public static void RecordBattle(PlayerSetupData attacker, PlayerSetupData defender,
            bool attackerAlive, bool defenderAlive)
        {
            if (!_active)
                return;
            int outcome = attackerAlive == defenderAlive ? 0 : attackerAlive ? 1 : -1;
            AddBattle(attacker, defender, outcome);
            AddBattle(defender, attacker, -outcome);
        }

        // BattleScreenUI.ResolveRetreat — the retreating side gave up the fight.
        public static void RecordRetreat(PlayerSetupData retreating, PlayerSetupData other, bool destroyed)
        {
            if (!_active)
                return;
            AddBattle(retreating, other, -1);
            AddBattle(other, retreating, 1);
            if (TryGet(retreating, out PlayerStats s))
            {
                s.Retreats++;
                if (destroyed) s.RetreatDestroyed++;
            }
        }

        // BuildingRegistry.Register while a match is running — a newly constructed building
        // (citadels are placed before BeginMatch; captures never re-register).
        public static void RecordBuilt(BuildingData building)
        {
            if (building == null || !TryGet(building.Owner, out PlayerStats s))
                return;
            if (building.IsBase) s.BasesBuilt++;
            else s.FacilitiesBuilt++;
        }

        // BuildingRegistry.CaptureOrDestroy — the one path every capture/destruction goes through.
        public static void RecordBuildingTaken(bool isBase, PlayerSetupData previousOwner, PlayerSetupData newOwner)
        {
            if (!_active)
                return;
            if (TryGet(newOwner, out PlayerStats winner))
            {
                if (isBase) winner.BasesCaptured++;
                else winner.FacilitiesDestroyed++;
            }
            if (TryGet(previousOwner, out PlayerStats loser))
            {
                if (isBase) loser.BasesLost++;
                else loser.FacilitiesLost++;
            }
        }

        public static void RecordElimination(PlayerSetupData player)
        {
            if (TryGet(player, out PlayerStats s) && s.EliminatedOnTurn == 0)
                s.EliminatedOnTurn = _turn;
        }

        public static void RecordGameOver(PlayerSetupData winner)
        {
            Finish(winner != null ? $"game over — {winner.Nickname} wins" : "game over — draw");
        }

        private static void Finish(string outcome)
        {
            if (!_active)
                return;
            if (_outcome == null)
                _outcome = outcome;
            foreach (PlayerSetupData p in Order)
                ByPlayer[p].Last = Capture(p);
            WriteSummary();
            AiDebugLog.Write($"[STATS] Match summary written ({_outcome}) — {SummaryRelativePath}");
            _active = false;
            CloseWriter();
        }

        private static void AddBattle(PlayerSetupData self, PlayerSetupData enemy, int outcome)
        {
            if (!TryGet(self, out PlayerStats s))
                return;
            bool vsNeutral = enemy == null || enemy.IsNeutral;
            if (outcome > 0) { if (vsNeutral) s.WinVsNeutral++; else s.WinVsPlayer++; }
            else if (outcome < 0) { if (vsNeutral) s.LossVsNeutral++; else s.LossVsPlayer++; }
            else { if (vsNeutral) s.DrawVsNeutral++; else s.DrawVsPlayer++; }
        }

        private static bool TryGet(PlayerSetupData player, out PlayerStats stats)
        {
            stats = null;
            return _active && player != null && ByPlayer.TryGetValue(player, out stats);
        }

        private static Snapshot Capture(PlayerSetupData p)
        {
            var snap = new Snapshot { Res = new int[4] };
            PlayerRoot root = PlayerRootRegistry.FindFor(p);
            for (int i = 0; i < 4; i++)
                snap.Res[i] = root != null ? root.GetResource(Types[i]) : 0;
            foreach (ArmyData a in ArmyRegistry.AllForOwner(p))
            {
                if (a == null || a.IsPrison || a.Members.Count == 0)
                    continue;
                snap.Units += a.Members.Count;
                if (!a.IsGarrison && !a.IsAirfield)
                    snap.FieldArmies++;
            }
            foreach (BuildingData b in BuildingRegistry.AllBuildings())
            {
                if (b == null || b.Owner != p)
                    continue;
                if (b.IsBase) snap.Bases++;
                else snap.Facilities++;
            }
            return snap;
        }

        private static void WriteSummary()
        {
            if (_summaryPath == null)
                return;
            var sb = new StringBuilder();
            sb.AppendLine($"=== AI match summary — started {_startedAt:yyyy-MM-dd HH:mm:ss}, rounds played: {_turn} ===");
            sb.AppendLine($"status: {_outcome ?? "in progress"}");
            sb.AppendLine();
            foreach (PlayerSetupData p in Order)
            {
                PlayerStats s = ByPlayer[p];
                Snapshot last = s.Last;
                int[] res = last.Res ?? new int[4];
                sb.AppendLine($"--- {p.Nickname} ({Kind(p)}){(s.EliminatedOnTurn > 0 ? $" — eliminated on round {s.EliminatedOnTurn}" : "")} ---");
                sb.AppendLine($"  now:        H/E/M/T={res[0]}/{res[1]}/{res[2]}/{res[3]}  fieldArmies={last.FieldArmies}  units={last.Units}  bases={last.Bases}  facilities={last.Facilities}");
                sb.AppendLine($"  peak:       bases={s.PeakBases}  fieldArmies={s.PeakFieldArmies}  units={s.PeakUnits}");
                sb.AppendLine($"  built:      bases={s.BasesBuilt}  facilities={s.FacilitiesBuilt}");
                sb.AppendLine($"  buildings:  basesCaptured={s.BasesCaptured}  basesLost={s.BasesLost}  facilitiesDestroyed={s.FacilitiesDestroyed}  facilitiesLost={s.FacilitiesLost}");
                sb.AppendLine($"  battles:    vsPlayers W/L/D={s.WinVsPlayer}/{s.LossVsPlayer}/{s.DrawVsPlayer}  vsNeutrals W/L/D={s.WinVsNeutral}/{s.LossVsNeutral}/{s.DrawVsNeutral}  retreats={s.Retreats} (destroyed {s.RetreatDestroyed})");
                if (s.AiTurns > 0)
                {
                    float leftover = s.ApStart > 0 ? (float)s.ApEnd / s.ApStart : 0f;
                    sb.AppendLine($"  AP:         turns={s.AiTurns}  avgStart={(float)s.ApStart / s.AiTurns:0.0}  avgSpent={(float)s.ApSpent / s.AiTurns:0.0}  "
                        + $"avgLeft={(float)s.ApEnd / s.AiTurns:0.0} ({leftover:P0} unused)  strandedTurns={s.StrandedTurns}");
                }
                if (!p.IsHuman)
                    sb.AppendLine($"  initiative: diceBought={s.DiceBought}  paid H/E/M/T={s.DiceSpent[0]}/{s.DiceSpent[1]}/{s.DiceSpent[2]}/{s.DiceSpent[3]}  suppressedTurns={s.InitiativeSuppressed}");
                sb.AppendLine();
            }
            try { File.WriteAllText(_summaryPath, sb.ToString()); }
            catch (Exception e)
            {
                Debug.LogWarning($"AiMatchStats: summary write failed, disabled for this match — {e.Message}");
                _summaryPath = null;
            }
        }

        private static void WriteTurnRow(string line)
        {
            if (_turnsWriter == null)
                return;
            try { _turnsWriter.WriteLine(line); }
            catch { CloseWriter(); }
        }

        private static void CloseWriter()
        {
            try { _turnsWriter?.Dispose(); }
            catch { /* best-effort */ }
            _turnsWriter = null;
        }

        private static string Kind(PlayerSetupData p) => p.IsHuman ? "human" : "ai";

        private static string Csv(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Contains(",") || s.Contains("\"") ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
