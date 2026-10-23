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
    //  WORLD ANALYSIS
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
    //
    //  CHEAT BOUNDARY: cheat data lives only in TrueWorld and in Cheat-sourced EnemyContactSnapshots.
    //  A Cheat contact is structurally forbidden a Position (see MakeCheatContact) — spec-18 as a
    //  type invariant.
    // ===========================================================================================
    //
    //  This file keeps only the entry points (Scan / RefreshOperationalState /
    //  RefreshStrategicKnowledge) that orchestrate the per-family builders below. Each snapshot
    //  family's builder + its own-only helpers live in the sibling WorldAnalysis.<Family>.cs
    //  partial file (Observation / Self / Development / Knowledge / Economy / Threat).
    public static partial class WorldAnalysis
    {
        // `intents` - the observer's durable intent store, bound read-only to the snapshot.
        internal static WorldSnapshot Scan(PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            IMissionIntentView intents)
        {
            using var __profile = new Game.Core.ProfileScope("AI/Analysis.Scan");
            AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
            var snap = new WorldSnapshot
            {
                Observer = player,
                Intents = intents,
                TurnNumber = ctx.TurnNumber,
                KnowledgeVersion = AiMapMemory.KnowledgeVersionFor(player),
                Map = ctx.Map,
                MapPathingVersion = ctx.Map != null ? ctx.Map.PathingVersion : -1,
            };
            // Self's garrison floor reads the observation clock/history. Advance them before
            // building Self, so the initial scan and later live donor checks agree this turn.
            AiReconMemory.Observe(player, ctx.TurnNumber, snap.KnowledgeVersion,
                AiMapMemory.AllKnownEnemySightings(player)
                    .Where(s => s.Owner == null || !s.Owner.IsEliminated), HideableHex(ctx));
            using (new Game.Core.ProfileScope("AI/Analysis.BuildSelf"))
                snap.Self = BuildSelf(player, root, hand, ctx, snap.Intents);
            using (new Game.Core.ProfileScope("AI/Analysis.BuildDevelopment"))
                snap.Development = BuildDevelopment(player, root, hand, ctx, snap.Intents);
            using (new Game.Core.ProfileScope("AI/Analysis.BuildKnown"))
                snap.Known = BuildKnown(player, snap.Self.BaseHexes);
            using (new Game.Core.ProfileScope("AI/Analysis.BuildTrueWorld"))
                snap.TrueWorld = BuildTrueWorld(player, ctx);
            using (new Game.Core.ProfileScope("AI/Analysis.BuildMapKnowledge"))
                snap.MapKnowledge = BuildMapKnowledge(player, ctx, snap);
            using (new Game.Core.ProfileScope("AI/Analysis.BuildEconomy"))
                snap.Economy = BuildEconomy(player, root, ctx, snap);
            snap.Threat = BuildThreat(player, ctx, snap);
            LogSnapshot(player, snap);
            CompleteDevelopmentPreparation(snap, player, root, hand, ctx);
            return snap;
        }

        // A rescan for the same observer keeps the store the previous snapshot was bound to.
        // Public terrain only: where an unseen army could stand (AiReconMemory.GoneOnReobservation).
        private static System.Func<HexCoord, bool, bool> HideableHex(AiTurnContext ctx)
        {
            HexMap map = ctx?.Map;
            return ReferenceEquals(map, null) ? null
                : (System.Func<HexCoord, bool, bool>)((hex, isAir) => map.CanEnter(hex, isAir));
        }

        private static IMissionIntentView IntentsCarriedFrom(WorldSnapshot prev, PlayerSetupData player) =>
            prev != null && object.ReferenceEquals(prev.Observer, player) ? prev.Intents : null;

        public static WorldSnapshot RefreshOperationalState(WorldSnapshot prev, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            // Snapshot ownership is explicit. Never reuse Known/MapKnowledge captured for a
            // different observer (or a different turn) merely because a caller supplied it.
            if (prev == null || !object.ReferenceEquals(prev.Observer, player)
                || ctx == null || prev.TurnNumber != ctx.TurnNumber
                || prev.Map != ctx.Map || prev.MapPathingVersion != (ctx.Map != null ? ctx.Map.PathingVersion : -1))
                return Scan(player, root, hand, ctx, IntentsCarriedFrom(prev, player));
            // Some callers refresh only operational facts between Phase-B actions. A play may
            // also reveal a site or defender: never pair fresh Self with a stale Known cache.
            if (prev.KnowledgeVersion != AiMapMemory.KnowledgeVersionFor(player))
                return RefreshStrategicKnowledge(prev, player, root, hand, ctx);

            var snap = new WorldSnapshot
            {
                Observer = player,
                Intents = prev.Intents,
                TurnNumber = prev.TurnNumber,
                KnowledgeVersion = prev.KnowledgeVersion,
                Map = prev.Map,
                MapPathingVersion = prev.MapPathingVersion,
                Known = prev.Known,
                MapKnowledge = prev.MapKnowledge,
            };
            using (new Game.Core.ProfileScope("AI/Analysis.BuildSelf"))
                snap.Self = BuildSelf(player, root, hand, ctx, snap.Intents);
            using (new Game.Core.ProfileScope("AI/Analysis.BuildDevelopment"))
                snap.Development = BuildDevelopment(player, root, hand, ctx, snap.Intents);
            // Operational combat facts must follow the registries even when honest knowledge did
            // not change (HP loss, reinforcement, destruction). This is materially cheaper than
            // rebuilding MapKnowledge and keeps the sanctioned TrueWorld boundary explicit.
            using (new Game.Core.ProfileScope("AI/Analysis.BuildTrueWorld"))
                snap.TrueWorld = BuildTrueWorld(player, ctx);
            using (new Game.Core.ProfileScope("AI/Analysis.BuildEconomy"))
                snap.Economy = BuildEconomy(player, root, ctx, snap);
            snap.Threat = BuildThreat(player, ctx, snap);

            CompleteDevelopmentPreparation(snap, player, root, hand, ctx);
            return snap;
        }

        public static WorldSnapshot RefreshStrategicKnowledge(WorldSnapshot prev, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            using var __profile = new Game.Core.ProfileScope("AI/Analysis.RefreshStrategicKnowledge");
            if (prev == null || !object.ReferenceEquals(prev.Observer, player)
                || ctx == null || prev.TurnNumber != ctx.TurnNumber
                || prev.Map != ctx.Map || prev.MapPathingVersion != (ctx.Map != null ? ctx.Map.PathingVersion : -1))
                return Scan(player, root, hand, ctx, IntentsCarriedFrom(prev, player));

            // Stamp before choosing the identity: observation metadata can advance knowledge.
            AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
            int knowledgeVersion = AiMapMemory.KnowledgeVersionFor(player);
            if (prev.KnowledgeVersion == knowledgeVersion)
                return RefreshOperationalState(prev, player, root, hand, ctx);

            var snap = new WorldSnapshot
            {
                Observer = player,
                Intents = prev.Intents,
                TurnNumber = prev.TurnNumber,
                KnowledgeVersion = knowledgeVersion,
                Map = ctx.Map,
                MapPathingVersion = ctx.Map != null ? ctx.Map.PathingVersion : -1,
            };
            AiReconMemory.Observe(player, ctx.TurnNumber, snap.KnowledgeVersion,
                AiMapMemory.AllKnownEnemySightings(player)
                    .Where(s => s.Owner == null || !s.Owner.IsEliminated), HideableHex(ctx));
            using (new Game.Core.ProfileScope("AI/Analysis.BuildSelf"))
                snap.Self = BuildSelf(player, root, hand, ctx, snap.Intents);
            using (new Game.Core.ProfileScope("AI/Analysis.BuildDevelopment"))
                snap.Development = BuildDevelopment(player, root, hand, ctx, snap.Intents);
            using (new Game.Core.ProfileScope("AI/Analysis.BuildKnown"))
                snap.Known = BuildKnown(player, snap.Self.BaseHexes);
            using (new Game.Core.ProfileScope("AI/Analysis.BuildTrueWorld"))
                snap.TrueWorld = BuildTrueWorld(player, ctx);
            using (new Game.Core.ProfileScope("AI/Analysis.BuildMapKnowledge"))
                snap.MapKnowledge = BuildMapKnowledge(player, ctx, snap);
            using (new Game.Core.ProfileScope("AI/Analysis.BuildEconomy"))
                snap.Economy = BuildEconomy(player, root, ctx, snap);
            snap.Threat = BuildThreat(player, ctx, snap);

            CompleteDevelopmentPreparation(snap, player, root, hand, ctx);
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

