using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  DECISIVE STRUCTURE PRESSURE
    // ===========================================================================================
    //  V2 Raid is army-targeted. Once the opponent's field contacts disappear, that used to make
    //  Aggression lose every concrete target even when the enemy's starting Citadel had already
    //  been honestly discovered and our military build-out was overwhelming. This narrow fallback
    //  closes that dead zone WITHOUT introducing omniscience or a parallel battle estimator:
    //
    //    · target must be an enemy starting Citadel already present in AiMapMemory;
    //    · that owner must have no currently remembered enemy army (if one exists, ordinary Raid
    //      remains the owner of combat target selection);
    //    · no durable Raid intent may already own a field combat actor;
    //    · military potential must be saturated according to the SAME potential-saturation ramp
    //      the Aggression war-pressure evaluator uses;
    //    · only an uncommitted, real ground field army with movement can advance;
    //    · movement is one safe step at a time through the canonical MoveArmyRoutine. A newly
    //      revealed contact/battle immediately stops this fallback and the next V2 evaluation sees
    //      the ordinary honest army target.
    //
    //  This is deliberately "advance to a known strategic structure", not "pretend the structure
    //  has zero defenders and force a battle". If the Citadel is undefended, the existing movement
    //  takeover seam captures/destroys it. If a defender is revealed, normal combat takes over.
    // ===========================================================================================
    internal sealed class StrategicPressurePlan
    {
        public ArmyData Army;
        public HexCoord TargetHex;
        public PlayerSetupData TargetOwner;
        public float Saturation;
    }

    internal static class StrategicPressureAdvance
    {
        // AI-MGR-02 — BuildPlan != null IS the candidate check; the end-of-turn tempo arbiter calls
        // it directly and ranks the advance against every other spend. No separate "preserve AP"
        // predicate any more.
        public static StrategicPressurePlan BuildPlan(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, ActorCommitments commitments)
        {
            if (player == null || root == null || hand == null || ctx == null)
                return null;

            if (MissionIntentRegistry.GetOrCreate(player).All.Any(i => i != null
                && i.Kind == MissionKind.Raid && i.Status == IntentStatus.Active))
                return null;

            List<AiMapMemory.KnownBuilding> citadels = AiMapMemory.AllKnownBuildings(player)
                .Where(b => b.Owner != null && b.Owner != player && !b.Owner.IsNeutral
                    && !b.Owner.IsEliminated && b.IsStartingCitadel)
                .OrderBy(b => b.Hex.Q).ThenBy(b => b.Hex.R)
                .ToList();
            if (citadels.Count == 0)
                return null;

            // If an honest army sighting for that owner still exists, the regular Raid lane has a
            // concrete combat target and should remain authoritative. Structure pressure begins
            // only after that target pool is empty.
            citadels = citadels
                .Where(c => !AiMapMemory.AllKnownEnemySightings(player).Any(s => s.Owner == c.Owner))
                .ToList();
            if (citadels.Count == 0)
                return null;

            float saturation = PotentialSaturation(player, hand);
            if (saturation + AiConfigV2.allocatorSliceEpsilon < AiConfigV2.aggPotentialSatRampHi)
                return null;

            HashSet<int> claimed = commitments?.ClaimedArmyIdSet
                ?? new HashSet<int>(MissionIntentRegistry.GetOrCreate(player).All
                    .Where(i => i?.PreferredMoverArmyId != null)
                    .Select(i => i.PreferredMoverArmyId.Value));

            List<ArmyData> candidates = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && a.Members.Count > 0 && a.CurrentMovement > 0
                    && !a.IsPrison && !a.IsGarrison && !a.IsAirfield && !a.IsAirArmy
                    && !AiArmyRoles.IsSoloRecce(a) && !AiArmyRoles.IsSoloHeroAwaitingEscort(a)
                    && !claimed.Contains(a.Id)
                    && (a.HasActivatedThisTurn || root.CanSpendActionPoints(a.ActivationApCost)))
                .OrderByDescending(a => AiPower.EffectiveArmyPower(a.Members))
                .ThenBy(a => a.Id)
                .ToList();
            if (candidates.Count == 0)
                return null;

            // Preserve the same defensive floor Aggression's surplus calculation always keeps.
            float totalField = candidates.Sum(a => AiPower.EffectiveArmyPower(a.Members));
            ArmyData bestArmy = null;
            AiMapMemory.KnownBuilding bestTarget = default;
            int bestDistance = int.MaxValue;
            float bestPower = float.MinValue;

            foreach (ArmyData army in candidates)
            {
                float power = AiPower.EffectiveArmyPower(army.Members);
                if (totalField - power + AiConfigV2.allocatorSliceEpsilon < AiConfigV2.aggHomeGuardFloor
                    && candidates.Count > 1)
                    continue;

                foreach (AiMapMemory.KnownBuilding target in citadels)
                {
                    int distance = HexGridMath.Distance(army.Hex, target.Hex);
                    if (bestArmy == null || power > bestPower + AiConfigV2.allocatorSliceEpsilon
                        || (Mathf.Abs(power - bestPower) <= AiConfigV2.allocatorSliceEpsilon && distance < bestDistance)
                        || (Mathf.Abs(power - bestPower) <= AiConfigV2.allocatorSliceEpsilon && distance == bestDistance
                            && army.Id < bestArmy.Id))
                    {
                        bestArmy = army;
                        bestTarget = target;
                        bestDistance = distance;
                        bestPower = power;
                    }
                }
            }

            if (bestArmy == null)
                return null;
            return new StrategicPressurePlan
            {
                Army = bestArmy,
                TargetHex = bestTarget.Hex,
                TargetOwner = bestTarget.Owner,
                Saturation = saturation,
            };
        }

        public static IEnumerator Execute(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            StrategicPressurePlan plan, System.Action<bool> setChanged)
        {
            if (plan?.Army == null || player == null || root == null || ctx?.Map == null)
                yield break;

            ArmyData army = plan.Army;
            bool changed = false;
            int safety = Mathf.Max(1, army.CurrentMovement + 1);
            int steps = 0;
            AiDebugLog.Write($"[AI][V2] strategic pressure — advance army #{army.Id} \"{army.Name}\" "
                + $"toward known enemy Citadel @({plan.TargetHex.Q},{plan.TargetHex.R}); "
                + $"militarySaturation={plan.Saturation:0.00}");

            while (steps++ < safety)
            {
                army = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a != null && a.Id == plan.Army.Id);
                if (army == null || army.Members.Count == 0 || army.CurrentMovement <= 0)
                    break;
                if (army.Hex.Equals(plan.TargetHex))
                    break;
                if (!army.HasActivatedThisTurn && !root.CanSpendActionPoints(army.ActivationApCost))
                    break;

                HexCoord? next = SafeStepPathing.FindNextSafeStep(ctx.Map, army, plan.TargetHex);
                if (!next.HasValue)
                {
                    AiDebugLog.Write($"[AI][V2] strategic pressure — stop army #{army.Id}: no safe step toward Citadel");
                    break;
                }

                HexCoord before = army.Hex;
                var decision = AiDecision.Move(army, next.Value,
                    $"V2 strategic pressure — advance toward known enemy Citadel at ({plan.TargetHex.Q},{plan.TargetHex.R})", 0f);
                var trace = new AiMoveExecutionTrace();
                yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);

                army = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a != null && a.Id == plan.Army.Id);
                HexCoord after = army != null ? army.Hex : trace.EndHex;
                if (!after.Equals(before))
                    changed = true;
                if (trace.BattleOccurred || trace.HexEventOccurred || army == null || after.Equals(before))
                    break;
            }

            setChanged?.Invoke(changed);
            AiDebugLog.Write($"[AI][V2] strategic pressure — army #{plan.Army.Id} "
                + $"finished advance changed={(changed ? 1 : 0)}");
        }

        private static float PotentialSaturation(PlayerSetupData player, AiHandData hand)
        {
            var now = new List<AiPower.PowerUnit>();
            int nowCap = 3;
            foreach (ArmyData a in ArmyRegistry.AllForOwner(player).Where(a => a != null))
                foreach (UnitData u in a.Members.Where(u => u != null))
                {
                    now.Add(AiPower.ToPowerUnit(u));
                    if (u.IsHero && u.CommandRating > nowCap)
                        nowCap = u.CommandRating;
                }
            foreach (CardData c in hand.Hand.Where(c => c?.Definition != null && IsMilitary(c.Definition)))
            {
                now.Add(AiPower.ToPowerUnit(c.Definition));
                if (c.Definition.cardType == CardType.Hero && c.Definition.commandRating > nowCap)
                    nowCap = c.Definition.commandRating;
            }

            var ceiling = new List<AiPower.PowerUnit>(now);
            int ceilingCap = nowCap;
            foreach (CardDefinition d in hand.RemainingDeck.Where(d => d != null && IsMilitary(d)))
            {
                ceiling.Add(AiPower.ToPowerUnit(d));
                if (d.cardType == CardType.Hero && d.commandRating > ceilingCap)
                    ceilingCap = d.commandRating;
            }

            float bestNow = AiPower.BestStackPotential(now, nowCap);
            float totalPotential = AiPower.TotalMilitaryPotential(ceiling, ceilingCap);
            return totalPotential <= 0.001f ? 0f : Mathf.Clamp01(bestNow / totalPotential);
        }

        private static bool IsMilitary(CardDefinition d) =>
            d.cardType == CardType.Unit || d.cardType == CardType.Hero;
    }
}
