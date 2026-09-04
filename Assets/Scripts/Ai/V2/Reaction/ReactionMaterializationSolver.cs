using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Cards;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // P0 — the whole shortage must close, not just improve. A bounded DFS over legal
    // materialization candidates that operationally deliver the needed capability; accepts the
    // subset with the smallest total prep AP that drives Σ projected FieldCombatPower ≥
    // NumericPowerDeficit (and/or lands ≥ 1 hero) while staying inside every real bound.
    internal sealed class MaterializationClosure
    {
        public float TotalAp;          // Σ prep AP + bounded downstream AP reserve
        public ResourceCost Envelope;  // Σ prep H/E/M/T (may be null)
        public string Key;             // deterministic subset key
        public string Detail;
    }

    // ARCH-02 §24 — the reaction materialization solver, split out of StrategicReactionPass.
    // Given a discovered raid target with a canonical Hero / FieldCombatPower shortage it proves
    // whether a BOUNDED, jointly-feasible combination of legal materialization actions FULLY closes
    // that shortage inside the reaction budget. It composes the real primitives
    // (MaterializationCandidateBuilder enumeration, MaterializationConsumptionState, AiPower,
    // StrategicSpendability) — never a second planner. Body is verbatim from the former
    // StrategicReactionPass.ProjectMaterializationClosure.
    internal static class ReactionMaterializationSolver
    {
        // P1 (round 10) — candidate-WIDTH DoS valve; at realistic hand sizes it never truncates.
        private const int reactionMatPoolCap = 24;

        internal static MaterializationClosure ProjectMaterializationClosure(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, WorldSnapshot snap, AiHandData hand,
            ActorCommitments commitments, AggressionObjective objective, RaidOperationalReadiness readiness)
        {
            bool needPower = readiness.NeedsPower;
            bool needHero = readiness.NeedsHero;
            if (!needPower && !needHero)
                return null;

            float apAvail = root != null ? Mathf.Max(0f, root.ActionPoints) : 0f;
            float apCeiling = Mathf.Min(apAvail, (float)AiConfigV2.reactionReserveApCap);
            float downstreamAp = Mathf.Max(0f, AiConfigV2.reactionResponderMoveApEstimate);
            float prepCeiling = apCeiling - downstreamAp;
            if (prepCeiling < -0.001f)
                return null;

            // P1 — reaction preparation IS Phase A; the DFS depth bound is EXACTLY the Phase-A
            // per-call action limit the real reaction FulfillDemands runs under (single source of
            // truth), NOT the end-of-turn tempo caps (which FulfillDemands does not debit).
            // Generation stays bounded by the turn-scoped generation budget, which Phase A DOES
            // debit.
            StrategicTempoBudget budget = StrategicTempoBudget.For(player, ctx.TurnNumber);
            int maxActions = AiConfigV2.maxDemandFulfillmentActionsPerTurn;
            if (maxActions <= 0)
                return null;
            int genRemaining = AiConfigV2.maxGenerationActionsPerTurn - budget.GenerationAttemptsUsed;
            int handSlotBudget = hand != null ? Mathf.Max(0, hand.Capacity - hand.Hand.Count) : 0;

            CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);
            var reservation = new MaterializationReservation
            {
                GenerationAttemptsUsed = budget.GenerationAttemptsUsed,
            };
            var heroDemand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Aggression, Capability = CapabilityKind.Hero,
                DesiredAmount = 1, RequiredTraits = TraitPreference.None, MinimumFollowupAp = 0f,
                TargetHex = objective.LastKnownHex, Value = objective.BaseValue,
            };
            var powerDemand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Aggression, Capability = CapabilityKind.FieldCombatPower,
                DesiredAmount = Mathf.Max(1f, readiness.RequestedPower), RequiredTraits = TraitPreference.None,
                MinimumFollowupAp = 0f, TargetHex = objective.LastKnownHex, Value = objective.BaseValue,
            };

            HashSet<int> claimed = commitments?.ClaimedArmyIdSet ?? new HashSet<int>();

            // P0.2 (round 10) — the shortage (NumericPowerDeficit) is measured in the CANONICAL
            // own-force metric: ArmySnapshot.EffectiveArmyPower == AiPower.EffectiveArmyPower over
            // AiPower.PowerUnit (Attack/Defense/HP/Init/Resistance/Fate/Range/IsHero + full ability
            // multiplier + composition). The contribution MUST be a delta of that SAME metric —
            // NOT WorthIt.DefenderProfile, which is a lossy enemy/fog line (no Resistance/Fate,
            // Range forced to 1, IsHero=false, reduced abilities).
            //
            // P0.3 (round 10) — per target army also carry the projected physical CAPACITY (canonical
            // ArmyData.Capacity baseline + hero occupancy). Two individually-legal cards into the
            // SAME army with one free slot must not BOTH be accepted — execution would preflight-fail
            // the second. Conservative: one hero per army; a hero raises capacity by +1 (an under-
            // estimate — a real hero's CommandRating is ≥3). ProjectedRoster power is still counted
            // only for a raid-ELIGIBLE army (structural raid actor, unclaimed — mirrors CapabilityInventory).
            var armyState = new Dictionary<string,
                (List<AiPower.PowerUnit> seed, bool eligible, int freeNonHero, bool hasHero)>();
            (string key, bool eligible) ResolveArmy(MaterializationPlan p)
            {
                switch (p.Deploy.Kind)
                {
                    case DeploymentKind.ExistingArmy:
                    {
                        int id = p.Deploy.Army?.Id ?? -1;
                        string k = "existing:" + id;
                        if (!armyState.ContainsKey(k))
                        {
                            ArmySnapshot a = snap?.Self?.Armies?.FirstOrDefault(x => x != null && x.ArmyId == id);
                            ArmyData live = ArmyRegistry.AllForOwner(player)
                                .FirstOrDefault(x => x != null && x.Id == id);
                            bool elig = a != null && a.IsStructuralRaidActor
                                && !claimed.Contains(id);
                            armyState[k] = (
                                live?.Members != null
                                    ? live.Members.Select(m => AiPower.ToPowerUnit(m)).ToList()
                                    : new List<AiPower.PowerUnit>(),
                                elig,
                                live != null ? Mathf.Max(0, live.Capacity - live.Members.Count) : 0,
                                live != null && live.Members.Any(m => m != null && m.IsHero));
                        }
                        return (k, armyState[k].eligible);
                    }
                    case DeploymentKind.ReusableShell:
                    {
                        string k = "shell:" + (p.Deploy.Army?.Id ?? -1);
                        if (!armyState.ContainsKey(k))
                            armyState[k] = (new List<AiPower.PowerUnit>(), true, 2, false);
                        return (k, true);
                    }
                    default: // NewArmy — fresh solo non-hero unit (hero-only solo is excluded by
                             // CanDeliverDemandOperationally), structurally a raid actor.
                    {
                        string k = "new:" + p.StableKey;
                        if (!armyState.ContainsKey(k))
                            armyState[k] = (new List<AiPower.PowerUnit>(), true, 2, false);
                        return (k, true);
                    }
                }
            }
            AiPower.PowerUnit ProjectedUnit(MaterializationPlan p)
            {
                CardDefinition def = p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef;
                // ProjectMaterialization folds in equipment already-attached AND attached-by-this-plan;
                // with no equipment it returns the plain base line — one path for every chain kind.
                AiPower.ProjectedStrategicLine line = AiPower.ProjectMaterialization(p);
                return new AiPower.PowerUnit(Mathf.Max(0f, line.BasePower), def?.unitTypeTags, line.Range,
                    def != null && def.cardType == CardType.Hero);
            }

            // Legal candidate pool — the SAME enumeration the hand-follow-up probe uses.
            var pool = new List<(MaterializationPlan plan, float ap, bool deliversHero, bool isHeroPlan,
                string armyKey, bool armyEligible, AiPower.PowerUnit unit)>();
            foreach (MaterializationPlan p in MaterializationCandidateBuilder.EnumerateSurplusPlans(
                snap, player, root, hand, ctx, inv, commitments, reservation))
            {
                if (p == null) continue;
                bool dHero = MaterializationCandidateBuilder.CanDeliverDemandOperationally(p, heroDemand);
                bool dPower = MaterializationCandidateBuilder.CanDeliverDemandOperationally(p, powerDemand);
                if (!dHero && !dPower) continue;
                (string armyKey, bool elig) = ResolveArmy(p);
                CardDefinition bd = p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef;
                bool isHeroPlan = bd != null && bd.cardType == CardType.Hero;
                pool.Add((p, Mathf.Max(0f, p.ApCost), dHero, isHeroPlan, armyKey, elig, ProjectedUnit(p)));
            }
            if (pool.Count == 0)
                return null;
            pool = pool
                .OrderBy(c => c.ap).ThenBy(c => ResCostSum(c.plan.ResCost))
                .ThenBy(c => c.plan.StableKey, System.StringComparer.Ordinal)
                .Take(reactionMatPoolCap).ToList();

            float needPowerAmt = needPower ? Mathf.Max(0f, readiness.NumericPowerDeficit) : 0f;
            const string owner = "reaction-budget:MaterializeForDiscovery";
            var consumed = new MaterializationConsumptionState();

            float bestPrepAp = float.MaxValue, bestEnvSum = float.MaxValue;
            ResourceCost bestEnv = null;
            string bestKey = null;
            var chosen = new List<(MaterializationPlan plan, float ap, bool deliversHero, bool isHeroPlan,
                string armyKey, bool armyEligible, AiPower.PowerUnit unit)>();

            // Σ over touched eligible armies of (EffectiveArmyPower(seed + added units) − seed),
            // recomputed from scratch at each node — cheap (pool ≤ 24, depth ≤ 3) and free of
            // incremental-bookkeeping bugs. SAME metric as NumericPowerDeficit.
            float ProjectedFieldPowerDelta()
            {
                float total = 0f;
                foreach (var g in chosen.GroupBy(c => c.armyKey))
                {
                    if (!g.First().armyEligible) continue;
                    List<AiPower.PowerUnit> seed = armyState[g.Key].seed ?? new List<AiPower.PowerUnit>();
                    float before = AiPower.EffectiveArmyPower(seed);
                    var after = new List<AiPower.PowerUnit>(seed);
                    foreach (var c in g) after.Add(c.unit);
                    total += Mathf.Max(0f, AiPower.EffectiveArmyPower(after) - before);
                }
                return total;
            }

            // P0.3 — would `chosen` + `extra` still be physically placeable? One hero per recipient
            // army; non-hero slots bounded by the projected free capacity; the combined generate-
            // chain hand-slot peak within the free hand.
            bool RecipientCapacityOk(
                (MaterializationPlan plan, float ap, bool deliversHero, bool isHeroPlan,
                 string armyKey, bool armyEligible, AiPower.PowerUnit unit) extra)
            {
                int handPeak = extra.plan.HandSlotsNeededAtPeak;
                foreach (var c in chosen) handPeak += c.plan.HandSlotsNeededAtPeak;
                if (handPeak > handSlotBudget)
                    return false;
                foreach (var g in chosen.Append(extra).GroupBy(c => c.armyKey))
                {
                    var st = armyState[g.Key]; // ResolveArmy always populates this for every pool key
                    int heroesAdded = 0, nonHeroAdded = 0;
                    foreach (var c in g) { if (c.isHeroPlan) heroesAdded++; else nonHeroAdded++; }
                    if ((st.hasHero ? 1 : 0) + heroesAdded > 1)
                        return false;
                    int nonHeroCap = st.freeNonHero + (heroesAdded > 0 ? 1 : 0);
                    if (nonHeroAdded > nonHeroCap)
                        return false;
                }
                return true;
            }

            void Consider()
            {
                bool powerOk = !needPower || ProjectedFieldPowerDelta() + 0.001f >= needPowerAmt;
                bool heroOk = !needHero || chosen.Any(c => c.deliversHero);
                if (!powerOk || !heroOk)
                    return;
                if (consumed.ApUsed > prepCeiling + 0.001f)
                    return;
                var env = new ResourceCost
                {
                    human = consumed.HumanUsed, energy = consumed.EnergyUsed,
                    materials = consumed.MaterialsUsed, tech = consumed.TechUsed,
                };
                if (!StrategicSpendability.FitsSpendableResources(player, root, ctx, env, owner))
                    return;
                float envSum = ResCostSum(env);
                if (consumed.ApUsed < bestPrepAp - 0.001f
                    || (consumed.ApUsed <= bestPrepAp + 0.001f && envSum < bestEnvSum - 0.001f))
                {
                    bestPrepAp = consumed.ApUsed;
                    bestEnvSum = envSum;
                    bestEnv = envSum > 0f ? env : null;
                    bestKey = string.Join("+",
                        chosen.Select(c => c.plan.StableKey).OrderBy(k => k, System.StringComparer.Ordinal));
                }
            }

            void Dfs(int start)
            {
                Consider();
                if (chosen.Count >= maxActions)
                    return;
                for (int i = start; i < pool.Count; i++)
                {
                    var c = pool[i];
                    if (!consumed.CardsDisjoint(c.plan)) continue;
                    if (c.plan.Generation != null && consumed.GenerationAttempts + 1 > genRemaining) continue;
                    if (consumed.ApUsed + c.ap > prepCeiling + 0.001f) continue;
                    if (!RecipientCapacityOk(c)) continue;
                    MaterializationConsumptionState.Token token = consumed.Push(c.plan);
                    chosen.Add(c);
                    Dfs(i + 1);
                    chosen.RemoveAt(chosen.Count - 1);
                    consumed.Pop(token);
                }
            }
            Dfs(0);

            if (bestKey == null)
                return null;

            float total = bestPrepAp + downstreamAp;
            string envStr = bestEnv == null ? "-"
                : $"H{bestEnv.human} E{bestEnv.energy} M{bestEnv.materials} T{bestEnv.tech}";
            string shortage = needHero && needPower ? $"Hero+power≥{needPowerAmt:0.#}"
                : needHero ? "Hero" : $"power≥{needPowerAmt:0.#}";
            return new MaterializationClosure
            {
                TotalAp = total,
                Envelope = bestEnv,
                Key = bestKey,
                Detail = $"materializeForDiscovery witness: raid #{objective.TargetArmyId} shortage {shortage} "
                    + $"closed by [{bestKey}] — prep {bestPrepAp:0.#} AP + downstream {downstreamAp:0.#} AP "
                    + $"+ envelope [{envStr}] (projected RaidAvailableFieldPower Δ ≥ deficit)",
            };
        }

        private static float ResCostSum(ResourceCost c) => c == null ? 0f
            : c.human + c.energy + c.materials + c.tech;
    }
}
