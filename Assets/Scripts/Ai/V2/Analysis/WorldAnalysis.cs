using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Core;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  WORLD ANALYSIS  (Strategy V2 build-order step 2, 2026-08-29)
    // ===========================================================================================
    //  Builds the one shared WorldSnapshot at the top of Pipeline.RunTurn. Everything downstream
    //  reads that object and never touches raw game state again.
    //
    //  PORTED FROM V1 (adapted, not rewritten):
    //    - AiStrategyDirector.Evaluate's "shared readings" block   -> BuildSelf / BuildKnown / BuildMapKnowledge
    //    - IncomeProjection.IncomeFor / TotalIncome                    -> BuildSelf.PerTurnIncome / BuildEconomy
    //    - AiDefencePlanner.CheatEstimateRaiderThreat (its SCOPE)  -> BuildThreat cheat-contact loop
    //      (the private method itself is left untouched in V1; V2 re-derives the same scan from
    //       TrueWorld.EnemyArmies using the SAME AiConfig radii/shape constants so the two can't
    //       silently diverge on the numbers)
    //    - AiDefencePlanner.DynamicPatrolUrgencyScore             -> NOT ported. Its job (a Patrol
    //      urgency score) is replaced by continuous AssetThreatSnapshot.Severity; Patrol/Intercept
    //      mission value is MissionLayer's problem, from expected Severity reduction.
    //    - AiDefencePlanner.IsUnderSiege                          -> OR'd into ThreatModel.UnderSiege
    //
    //  CHEAT BOUNDARY: cheat data lives only in TrueWorld and in Cheat-sourced EnemyContactSnapshots.
    //  A Cheat contact is structurally forbidden a Position (see MakeCheatContact) — spec-18 as a
    //  type invariant.
    // ===========================================================================================
    //
    //  File-split (mechanical, no behaviour change, Docs/ai-v2-file-split-refactor-tasks.md Task 5):
    //  this file keeps only the entry points (Scan / RefreshOperationalState /
    //  RefreshStrategicKnowledge) that orchestrate the per-family builders below. Each snapshot
    //  family's builder + its own-only helpers live in the sibling WorldAnalysis.<Family>.cs
    //  partial file (Observation / Self / Development / Knowledge / Economy / Threat).
    public static partial class WorldAnalysis
    {
        public static WorldSnapshot Scan(PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            var snap = new WorldSnapshot { TurnNumber = ctx.TurnNumber };
            snap.Self = BuildSelf(player, root, hand, ctx);
            snap.Development = BuildDevelopment(player, root, hand, ctx);
            snap.Known = BuildKnown(player, snap.Self.BaseHexes);
            AiReconMemory.Observe(player, ctx.TurnNumber, snap.Known.EnemySightings);
            snap.TrueWorld = BuildTrueWorld(player, ctx);
            snap.MapKnowledge = BuildMapKnowledge(player, ctx, snap);
            snap.Economy = BuildEconomy(player, root, ctx, snap);
            snap.Development.ProductionSupport = DevelopmentReadiness.CalculateProductionSupport(
                snap.Economy, snap.Development.SurplusFraction);
            snap.Threat = BuildThreat(player, ctx, snap);
            LogSnapshot(player, snap);
            return snap;
        }

        public static WorldSnapshot RefreshOperationalState(WorldSnapshot prev, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            if (prev == null)
                return Scan(player, root, hand, ctx);

            var snap = new WorldSnapshot
            {
                TurnNumber = prev.TurnNumber,
                Known = prev.Known,
                TrueWorld = prev.TrueWorld,
                MapKnowledge = prev.MapKnowledge,
            };
            snap.Self = BuildSelf(player, root, hand, ctx);
            snap.Development = BuildDevelopment(player, root, hand, ctx);
            snap.Economy = BuildEconomy(player, root, ctx, snap);
            snap.Development.ProductionSupport = DevelopmentReadiness.CalculateProductionSupport(
                snap.Economy, snap.Development.SurplusFraction);
            snap.Threat = BuildThreat(player, ctx, snap);

            SelfSnapshot s = snap.Self;
            AiDebugLog.WriteVerbose($"[AI][V2] {player?.Nickname} op-refresh — AP {s.ActionPoints} "
                + $"hand {s.Hand.Count}/{s.HandCapacity} armies {s.Armies.Count} "
                + $"field {F(s.FieldPower)} garrison {F(s.GarrisonPower)} "
                + $"bestStack {F(s.BestStackPotential)} threats {snap.Threat?.Threats?.Count ?? 0}");
            return snap;
        }

        public static WorldSnapshot RefreshStrategicKnowledge(WorldSnapshot prev, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            if (prev == null)
                return Scan(player, root, hand, ctx);

            var snap = new WorldSnapshot { TurnNumber = prev.TurnNumber };
            snap.Self = BuildSelf(player, root, hand, ctx);
            snap.Development = BuildDevelopment(player, root, hand, ctx);
            snap.Known = BuildKnown(player, snap.Self.BaseHexes);
            AiReconMemory.Observe(player, ctx.TurnNumber, snap.Known.EnemySightings);
            snap.TrueWorld = BuildTrueWorld(player, ctx);
            snap.MapKnowledge = BuildMapKnowledge(player, ctx, snap);
            snap.Economy = BuildEconomy(player, root, ctx, snap);
            snap.Development.ProductionSupport = DevelopmentReadiness.CalculateProductionSupport(
                snap.Economy, snap.Development.SurplusFraction);
            snap.Threat = BuildThreat(player, ctx, snap);

            AiDebugLog.WriteVerbose($"[AI][V2] {player?.Nickname} knowledge-refresh — "
                + $"enemyKnown {snap.Known.EnemySightings.Count} neutralKnown {snap.Known.NeutralSightings.Count} "
                + $"visited {snap.MapKnowledge.VisitedHexes}/{snap.MapKnowledge.TotalHexes} "
                + $"frontier {snap.MapKnowledge.Frontier.Count} threats {snap.Threat.Threats.Count}");
            return snap;
        }


        // Settled observation boundary for one bounded task. Analysis owns factual comparison;
        // Orchestration only decides which typed task family consumes the published invalidation.
        internal sealed class StepObservationStamp
        {
            internal readonly WorldSnapshot Snapshot;
            internal readonly V2ResourceStamp Resources;
            internal readonly AiHandData Hand;
            internal readonly int HandVersion;

            internal StepObservationStamp(WorldSnapshot snapshot, V2ResourceStamp resources,
                AiHandData hand)
            {
                Snapshot = snapshot;
                Resources = resources;
                Hand = hand;
                HandVersion = hand?.MutationVersion ?? -1;
            }
        }

    }
}
