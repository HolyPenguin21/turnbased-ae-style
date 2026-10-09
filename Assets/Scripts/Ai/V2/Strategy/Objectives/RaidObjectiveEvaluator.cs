using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using Game.HexGrid;
using Game.Combat;
using UnityEngine;
using Game.Core;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // The single Raid objective owner: discovery, intrinsic score, honest target facts,
    // validity and completion. Fight/admission decisions remain with GroundCombat and the
    // lane's operational-readiness policy; durable lifecycle remains with Continuity.
    public static class RaidObjectiveEvaluator
    {
        // ---- SNAPSHOT (continuity / mission-layer re-materialisation) -----------------------

        // Is the tracked target still a coherent thing to raid? Reads ONLY the snapshot's honest
        // knowledge: sightings for a NeutralArmy target, the remembered active guard for an
        // EventGuard target — never a live army-ownership system or the live event registry.
        public static bool IsIntentStillValid(WorldSnapshot snap, RaidIntent intent)
        {
            if (snap?.Known == null || intent == null || !intent.Target.HasValue)
                return false;

            if (intent.Target.Kind == RaidTargetKind.EventGuard)
                return snap.Known.EventGuardHexes != null
                    && snap.Known.EventGuardHexes.Contains(intent.Target.Hex);

            AiMapMemory.KnownEnemySighting? s = FindSighting(snap, intent.Target.ArmyId);
            if (s == null)
                // No current honest sighting. Keep the intent alive as long as it has actually
                // started (a Hard raid in transit must not evaporate the turn the target slips
                // into fog) — MissionContinuityLayer's stall / age caps still reap a raid that
                // never re-acquires. An unstarted intent with no sighting is dropped.
                return intent.OperationStarted;

            // Raid targets NEUTRALS ONLY (ActiveDefence and Attack own enemy armies). A target that
            // turned into ANY non-neutral player's army — ours or a third player's — ends this
            // objective.
            return IsNeutralRaidTarget(s.Value.Owner);
        }

        // The ONE canonical "is this still a legal Raid target" ownership check.
        // A null owner is an unclaimed neutral encounter army. Everything downstream (Provisioning,
        // Execution) must call THIS, not re-derive its own neutrality rule.
        public static bool IsNeutralRaidTarget(PlayerSetupData owner) => owner == null || owner.IsNeutral;

        // Snapshot-pure completion edge for the campaign phase machine. Loss of sight is UNKNOWN;
        // a present sighting with a non-neutral owner is positive proof that this target no longer
        // belongs to Raid and must trigger next-neutral/Return handling. An event guard can never
        // change owner (it has no owner concept until spawned, and is torn down on defeat) — always
        // false for that kind.
        public static bool IsKnownTargetNoLongerNeutral(WorldSnapshot snap, RaidTargetRef target)
        {
            if (!target.HasValue || target.Kind == RaidTargetKind.EventGuard)
                return false;
            AiMapMemory.KnownEnemySighting? sighting = FindSighting(snap, target.ArmyId);
            return sighting.HasValue && !IsNeutralRaidTarget(sighting.Value.Owner);
        }

        // The tracked target's freshest honest sighting, or null. Physical-army lookup only —
        // callers must branch on RaidTargetKind BEFORE calling this for an EventGuard target.
        public static AiMapMemory.KnownEnemySighting? FindSighting(WorldSnapshot snap, int trackedArmyId) =>
            FindSightingIn(snap?.Known?.EnemySightings, snap?.Known?.NeutralSightings, trackedArmyId);

        // ---- LIVE (post-execution ledger pass) --------------------------------------------

        // Objective completion must be POSITIVE, not inferred from one registry's absence.
        //  NeutralArmy:
        //   1) If the target id is now ours, it was captured -> satisfied.
        //   2) If any ordinary non-us player now fields it, it is no longer neutral and therefore
        //      no longer a legal Raid target -> satisfied for this campaign objective.
        //   3) If no ordinary roster resolves it but honest neutral memory still tracks it, this is
        //      the neutral/fog case -> not satisfied.
        //   4) Only absence from both live ownership and honest memory counts as confirmed gone.
        //  EventGuard:
        //   Satisfied exactly when OUR memory confirms the event completed — by us (known at once)
        //   or by another player, learned only on a re-observation of the hex. Disappearance from
        //   visibility, or a global Consumed flag we have not observed, has no bearing.
        public static bool IsObjectiveSatisfiedLive(PlayerSetupData player, RaidTargetRef target)
        {
            if (player == null || !target.HasValue)
                return false;

            if (target.Kind == RaidTargetKind.EventGuard)
                return AiMapMemory.KnownEventStateAt(player, target.Hex) == AiMapMemory.KnownEventState.Completed;

            int targetArmyId = target.ArmyId;
            if (ArmyRegistry.AllForOwner(player)
                .Any(a => a != null && a.Id == targetArmyId && a.Members.Count > 0))
                return true;

            foreach (PlayerSetupData other in GameSession.Players ?? System.Linq.Enumerable.Empty<PlayerSetupData>())
            {
                if (other == null || other.Equals(player))
                    continue;
                if (ArmyRegistry.AllForOwner(other)
                    .Any(a => a != null && a.Id == targetArmyId && a.Members.Count > 0))
                    return true;
            }

            bool rememberedEnemy = AiMapMemory.AllKnownEnemySightings(player)
                .Any(s => s.ArmyId == targetArmyId);
            bool rememberedNeutral = AiMapMemory.AllKnownNeutralSightings(player)
                .Any(s => s.ArmyId == targetArmyId);
            return !rememberedEnemy && !rememberedNeutral;
        }

        // The one answer to "what do WE know about this event target" for Provisioning/Execution:
        // Active = remembered, still pending; Completed = we confirmed it is done; Unknown = we hold
        // no personal knowledge of it (never actionable).
        public static AiMapMemory.KnownEventState EventTargetState(PlayerSetupData player, HexCoord hex) =>
            AiMapMemory.KnownEventStateAt(player, hex);

        public static List<RaidObjective> Enumerate(WorldSnapshot snap, CombatOpportunityReport report)
        {
            using var __profile = new Game.Core.ProfileScope("AI/Objectives.Aggression");
            var list = new List<RaidObjective>();
            if (snap?.Self == null)
            {
                AiDebugLog.Write("[AI][V2][RaidObjective] decision=NONE reason=no_self_snapshot");
                return list;
            }
            IReadOnlyList<CombatOpportunity> candidates = report?.NeutralOpportunities
                ?? (IReadOnlyList<CombatOpportunity>)System.Array.Empty<CombatOpportunity>();
            if (candidates.Count == 0)
            {
                AiDebugLog.Write("[AI][V2][RaidObjective] decision=NONE reason=no_known_neutral_army_opportunities");
                return list;
            }

            foreach (CombatOpportunity o in candidates)
            {
                if (!o.TargetIsNeutral)
                {
                    AiDebugLog.WriteDeduped(o.Target.DiagnosticLabel,
                        $"[AI][V2][RaidObjective] decision=REJECT target={o.Target.DiagnosticLabel} reason=target_is_not_neutral");
                    continue;
                }
                if (!o.HasTarget || !o.Target.HasValue)
                {
                    AiDebugLog.WriteDeduped("None",
                        "[AI][V2][RaidObjective] decision=REJECT target=None reason=opportunity_has_no_target");
                    continue;
                }

                RaidObjective obj = Build(snap, report, o);
                if (obj.BaseValue < AiConfigV2.raidObjectiveMinBaseValue)
                {
                    // Re-evaluated every cycle for every below-threshold candidate — WriteDeduped
                    // keeps the reject reason visible without reprinting an unchanged value each time.
                    AiDebugLog.WriteDeduped(obj.Target.DiagnosticLabel,
                        $"[AI][V2][RaidObjective] decision=REJECT target={obj.Target.DiagnosticLabel} "
                        + $"reason=task_value_below_threshold value={F(obj.BaseValue)} min={F(AiConfigV2.raidObjectiveMinBaseValue)}");
                    continue;
                }

                list.Add(obj);
                string frozenGap = !obj.CanCoverAllDefenders ? "coverage"
                    : obj.NeedsCombatPower ? "assemblability"
                    : obj.NeedsHero ? "hero_availability" : "none";
                AiDebugLog.WriteDeduped(obj.Target.DiagnosticLabel,
                    $"[AI][V2][RaidObjective] decision=ACCEPT target={obj.Target.DiagnosticLabel} "
                    + $"hex=({obj.LastKnownHex.Q},{obj.LastKnownHex.R}) task={F(obj.BaseValue)} "
                    + $"readyWin={F(obj.ReadyWinChance)} asmWin={F(obj.AssemblableWinChance)} "
                    + $"cover={(obj.CanCoverAllDefenders ? 1 : 0)} gate={(obj.GatePassed ? 1 : 0)} "
                    + $"defenders={obj.DefenderCount} frozenNeedsPower={(obj.NeedsCombatPower ? 1 : 0)} "
                    + $"frozenNeedsHero={(obj.NeedsHero ? 1 : 0)} frozenPowerDeficit={F(obj.CombatPowerDeficit)} "
                    + $"frozenGap={frozenGap}");
            }

            list.Sort((a, b) =>
            {
                int c = b.BaseValue.CompareTo(a.BaseValue);
                return c != 0 ? c : string.CompareOrdinal(a.Target.DiagnosticLabel, b.Target.DiagnosticLabel);
            });
            return list;
        }

        public static RaidObjective ForTrackedTarget(WorldSnapshot snap, CombatOpportunityReport report,
            RaidTargetRef target)
        {
            if (report?.All == null || !target.HasValue)
                return null;
            foreach (CombatOpportunity o in report.All)
                if (o.HasTarget && o.TargetIsNeutral && o.Target.Equals(target))
                    return Build(snap, report, o);
            return null;
        }

        internal static TaskScore BuildRaidScore(WorldSnapshot snap, RaidTargetRef target, HexCoord targetHex)
        {
            int homeDistance = TaskScoreEvaluator.NearestOwnedHomeDistance(snap, targetHex);
            // Home threat is an offensive-restraint fact of the task, not of the Aggression Radar
            // (which also carries ActiveDefence): a Raid away from a threatened Citadel waits.
            // A Raid is worth what clearing its hex gives (2026-10-07, user decision). Four
            // reasons exist, summed when they coincide:
            //   · a resource hex  — the facility income a free hex would pay (EconomicHexBenefit);
            //   · a Hex Event     — the event's own reward (EventReward), by the guard tier the
            //                       observer remembers;
            //   · a Base site     — the new resource cluster a Base there would open;
            //   · none of those   — nothing: only OwnTerritoryProximity remains (a shorter route
            //                       for our own armies), so a far plain neutral falls below
            //                       raidObjectiveMinBaseValue and is never gathered for.
            // Both site rewards fade with the distance from our base network
            // (EconomyBaseNetworkSynergy), so a Raid is never lured far from home by income.
            return new TaskScore(
                economicHexBenefit: ClearedResourceHexBenefit(snap, targetHex),
                ownTerritoryProximity: TaskScoreEvaluator.RaidProximity(homeDistance),
                eventReward: target.Kind == RaidTargetKind.EventGuard
                    ? TaskScoreEvaluator.EventReward(KnownEventGuardTier(snap, targetHex)) : 0f,
                economicExpansionValue: ClearedBaseSiteValue(snap, targetHex),
                citadelThreatRisk: TaskScoreEvaluator.CitadelThreatRisk(snap));
        }

        private static float ClearedResourceHexBenefit(WorldSnapshot snap, HexCoord hex)
        {
            EconomyStanding eco = snap?.Economy;
            if (eco?.PerType == null)
                return 0f;
            var perResource = new List<(float Gain, float Priority)>();
            float synergy = 0f;
            foreach (EconomyExtractionOpportunity site in eco.GuardedExtractionSites)
            {
                if (!site.Hex.Equals(hex))
                    continue;
                foreach (EconomyResourceStanding standing in eco.PerType)
                    if (standing.Type == site.ResourceType)
                        perResource.Add((standing.UsefulMarginalIncomeGain(site.MarginalIncomeGain),
                            TaskScoreEvaluator.ResourcePriority(standing)));
                synergy = Mathf.Max(synergy, site.BaseNetworkSynergy);
            }
            return TaskScoreEvaluator.EconomicHexBenefit(perResource) * synergy;
        }

        private static float ClearedBaseSiteValue(WorldSnapshot snap, HexCoord hex)
        {
            EconomyStanding eco = snap?.Economy;
            if (eco == null)
                return 0f;
            float best = 0f;
            foreach (GuardedBaseSite site in eco.GuardedBaseSites)
                if (site.Hex.Equals(hex))
                    best = Mathf.Max(best, TaskScoreEvaluator.EconomicExpansionValue(
                        site.NewResourceClusterHexes / AiConfigV2.economyBaseExpansionClusterFullCount));
            return best * WorldAnalysis.EconomyBaseNetworkSynergy(snap, hex);
        }

        // The remembered guard tier of the event on `hex` (AiMapMemory via Known.EventGuards);
        // -1 when this observer does not know it.
        private static int KnownEventGuardTier(WorldSnapshot snap, HexCoord hex)
        {
            if (snap?.Known?.EventGuards != null)
                foreach (KnownEventGuardSnapshot g in snap.Known.EventGuards)
                    if (g.Hex.Equals(hex))
                        return g.Strength.RewardTier;
            return -1;
        }

        private static RaidObjective Build(WorldSnapshot snap, CombatOpportunityReport report,
            CombatOpportunity o)
        {
            // Defender power is a combat-difficulty fact, not an expected resource/card reward.
            // Both neutral-army and guarded-event Raid objectives receive exactly one fixed
            // intrinsic reward. Combat difficulty remains with WorthIt and assembly.
            // Raid targets are stationary neutrals or event guards; older sightings do not move them.
            // IntelAgePenalty stays only for mobile targets (ActiveDefence); Raid and Attack never pay it.
            TaskScore score = BuildRaidScore(snap, o.Target, o.TargetHex);

            bool haveViable = o.IsViable;
            bool needsHero = !haveViable && !report.HeroAvailable;
            bool needsCombatPower = !haveViable;

            IReadOnlyList<WorthIt.DefenderProfile> knownDefenders = AiV2Util.KnownDefenders(snap, o.Target);
            float targetPower = AiPower.EffectiveArmyPowerFromProfiles(knownDefenders);
            float requiredPower = GroundCombatFeasibility.RequiredPower(knownDefenders,
                AiV2Util.KnownRaidDefenceBonus(snap, o.Target));
            float deficit = needsCombatPower ? Mathf.Max(1f, requiredPower - snap.Self.FieldPower) : 0f;

            return new RaidObjective
            {
                Kind = AggressionObjectiveKind.Raid,
                Target = o.Target,
                LastKnownHex = o.TargetHex,
                TargetOwner = o.TargetOwner,
                TargetIsNeutral = o.TargetIsNeutral,
                TaskScore = score,
                BaseValue = score.Value,
                Confidence = o.Confidence,
                ReadyWinChance = o.ReadyWinChance,
                AssemblableWinChance = o.AssemblableWinChance,
                CanCoverAllDefenders = o.CanCoverAllDefenders,
                EstimatedEta = o.Eta,
                DefenderCount = o.DefenderCount,
                TargetPower = targetPower,
                GatePassed = o.GatePassed,
                NeedsCombatPower = needsCombatPower,
                NeedsHero = needsHero,
                CombatPowerDeficit = deficit,
            };
        }

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        internal static AiMapMemory.KnownEnemySighting? FindSightingLive(
            PlayerSetupData player, int targetArmyId) =>
            FindSightingIn(AiMapMemory.AllKnownEnemySightings(player),
                AiMapMemory.AllKnownNeutralSightings(player), targetArmyId);

        private static AiMapMemory.KnownEnemySighting? FindSightingIn(
            IEnumerable<AiMapMemory.KnownEnemySighting> enemies,
            IEnumerable<AiMapMemory.KnownEnemySighting> neutrals, int armyId)
        {
            foreach (AiMapMemory.KnownEnemySighting sighting in
                (enemies ?? Enumerable.Empty<AiMapMemory.KnownEnemySighting>())
                    .Concat(neutrals ?? Enumerable.Empty<AiMapMemory.KnownEnemySighting>()))
                if (sighting.ArmyId == armyId)
                    return sighting;
            return null;
        }
    }
}

