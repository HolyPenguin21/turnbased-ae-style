using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // Stronghold pin — the last defender of a threatened own Citadel/Base stays, unless the
    // stronghold stays covered or it destroys the threat first; read by every ground-combat lane.
    public static partial class ActiveDefenceObjectiveEvaluator
    {
        // THE stronghold-hold rule, read by every ground-combat nomination
        // (GroundCombatActorEligibility), the assault provisioning gate and ActiveDefence
        // withdrawals: an army that is the last combat body on an own Citadel/Base which a known
        // hostile force able to damage it can reach within strongholdDefenderPinEnemyEta turns
        // stays on that hex. Leaving opens the stronghold to capture — a loss no TaskScore price
        // of the operation it would leave for can outweigh (Ysolde T9, 2026-09-28: the Citadel's
        // only defender left to intercept another enemy and the Citadel fell unopposed). Staying
        // still fights the arriving enemy, on the hex with its defence bonus.
        // `ignoreEnemyArmyId` — the enemy this army is asked to intercept (TryDirectResponse): a
        // defender held only by that enemy may go and destroy it before it arrives (the plan's
        // own gate + CanInterceptPlanBeforeThreat), 2026-11-15.
        internal static bool IsPinnedStrongholdDefender(WorldSnapshot snap, ArmySnapshot army,
            ISet<int> unavailable = null, IReadOnlyDictionary<int, ArmySnapshot> projectedArmies = null,
            int? ignoreEnemyArmyId = null)
        {
            if (army == null || army.IsAir || army.IsAirfield || army.IsPrison
                || army.EffectiveArmyPower <= AiConfigV2.allocatorSliceEpsilon
                || snap?.Self?.Armies == null || snap.Self.BaseHexes == null
                || !snap.Self.BaseHexes.Contains(army.Hex))
                return false;
            // Another body on the hex is not proof that the stronghold remains defended.
            // Reuse the same defending-side simulation as AssessResponse, excluding the mover.
            var without = unavailable == null ? new HashSet<int>() : new HashSet<int>(unavailable);
            without.Add(army.ArmyId);
            foreach (AssetThreatSnapshot threat in snap.Threat?.Threats ?? Enumerable.Empty<AssetThreatSnapshot>())
            {
                if (!IsPinCandidate(threat)
                    || !threat.Asset.Hex.Equals(army.Hex)
                    || !IsDefendableAsset(snap, threat.Asset.Kind, threat.Asset.Hex)
                    || !threat.EnemyEta.HasValue
                    || threat.EnemyEta.Value > AiConfigV2.strongholdDefenderPinEnemyEta
                    || (ignoreEnemyArmyId.HasValue && threat.Contact.Army.ArmyId == ignoreEnemyArmyId.Value))
                    continue;
                var objective = new ActiveDefenceObjective { Target = new ActiveDefenceMissionTarget
                    { ProtectedAssetHex = army.Hex, ProtectedAssetKind = threat.Asset.Kind } };
                var opposition = KnownOpposition.OfEnemyArmy(snap, threat.Contact.Army.ArmyId);
                if (opposition == null || FallsWithout(HoldChanceAtAsset(snap, objective, null, null,
                        opposition, without, projectedArmies), PinConfidence(snap, threat.Contact)))
                    return true;
            }
            return false;
        }

        // The Attack fist this objective's enemy keeps home ("fist #24"), or null. Only an observed
        // force within the withdraw horizon can be the pin (StrongholdNeedingArmy) and only an
        // observed one has a position to intercept. `interceptActor` — the army a live intercept of
        // this enemy already uses: the fist that went to remove its blocker is claimed by that
        // intercept now, yet it is still the army the force keeps from the Attack (a two-turn
        // intercept must not be dropped on its second turn).
        private static string BlockedAttackFist(WorldSnapshot snap, ActiveDefenceObjective objective,
            int? interceptActor = null)
        {
            ActiveDefenceMissionTarget t = objective.Target;
            if (!t.EnemyEta.HasValue || t.EnemyEta.Value > AiConfigV2.activeDefenceWithdrawMaxEnemyEta)
                return null;
            EnemyContactSnapshot contact = snap?.Threat?.Contacts?.FirstOrDefault(c => c?.Army != null
                && c.Army.ArmyId == t.EnemyArmyId);
            if (contact == null || contact.Knowledge != ContactKnowledge.Exact)
                return null;
            List<int> fists = AttackObjectiveEvaluator.AttackFistIds(snap);
            if (interceptActor.HasValue && !fists.Contains(interceptActor.Value))
                fists.Add(interceptActor.Value);
            foreach (int id in fists)
            {
                ArmySnapshot fist = snap.Self.Armies?.FirstOrDefault(a => a != null && a.ArmyId == id);
                if (fist != null && AttackMarchPolicy.IsBlockedBy(snap, fist, t.EnemyArmyId))
                    return "fist #" + id;
            }
            return null;
        }

        // ---- 2026-11-15 (owner, match Brynn vs Grimm) — THE pin facts, shared by
        // IsPinnedStrongholdDefender and AttackMarchPolicy.StrongholdNeedingArmy ------------------

        // A threat that can pin an army home: an honest positioned hostile that can damage the
        // asset AND can take a structure at all (a ground battle body; aircraft cannot).
        internal static bool IsPinCandidate(AssetThreatSnapshot t) =>
            IsHonestPositionedHostile(t) && t.CanDamage && WorldAnalysis.TakesStructures(t.Contact.Army);

        // How sure we are the remembered force is there to strike: 1 when observed now, else the
        // memory decay (AiReconMemory.ConfidenceDecay) - the rule the garrison floor reads.
        internal static float PinConfidence(WorldSnapshot snap, EnemyContactSnapshot c) =>
            c == null ? 0f : c.Knowledge == ContactKnowledge.Exact ? 1f
                : AiReconMemory.ConfidenceDecay(c.AgeTurns(snap?.TurnNumber ?? 0));

        // The stronghold falls without the army when the chance it is lost - the force is there
        // (confidence) and wins (1 - hold) - exceeds what activeDefenceHoldWinChance accepts.
        // At confidence 1 this is exactly hold < activeDefenceHoldWinChance.
        internal static bool FallsWithout(float holdWinChance, float confidence) =>
            (1f - Mathf.Clamp01(holdWinChance)) * Mathf.Clamp01(confidence)
                > 1f - AiConfigV2.activeDefenceHoldWinChance;

        // Can an own free field army destroy this threat force before it reaches the asset?
        // Only an observed force has a position to intercept; not one standing on its own
        // structure (that is the Attack lane). The same plan + timing seam the direct response
        // uses (PlanForArmyAtThreshold at the fresh-start gate, CanInterceptPlanBeforeThreat).
        // `excluded` - armies that are not free for it (other operations, the asking army).
        // Per snapshot: the march verdict asks it for every candidate of every planner pass; the
        // threats and armies are frozen with the snapshot (key: enemy, eta, excluded set).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldSnapshot,
            Dictionary<string, int>> s_defeatMemo = new();

        internal static bool CanDefeatBeforeThreat(WorldSnapshot snap, AssetThreatSnapshot t,
            ISet<int> excluded, out int defeaterArmyId)
        {
            defeaterArmyId = -1;
            EnemyContactSnapshot c = t?.Contact;
            if (c?.Army == null || c.Knowledge != ContactKnowledge.Exact || !c.Position.HasValue
                || !t.EnemyEta.HasValue || snap?.Self?.Armies == null
                || OnKnownForeignStructure(snap, c.Position.Value))
                return false;
            Dictionary<string, int> memo = s_defeatMemo.GetOrCreateValue(snap);
            string key = $"{c.Army.ArmyId}|{t.EnemyEta.Value}|{c.Position.Value.Q},{c.Position.Value.R}|"
                + string.Join(",", (excluded ?? (IEnumerable<int>)Array.Empty<int>()).OrderBy(id => id));
            if (!memo.TryGetValue(key, out defeaterArmyId))
                memo[key] = defeaterArmyId = FindDefeater(snap, t, c, excluded);
            return defeaterArmyId >= 0;
        }

        // 2026-11-18 (owner, match Tessek vs Halden) — may an Attack army that its own stronghold
        // calls home be handed to ActiveDefence instead: does THIS army destroy the threat before
        // it reaches the stronghold? The same plan, win gate and turn-order timing as
        // CanDefeatBeforeThreat, asked of this one army (every other army excluded), whatever
        // operation holds it now. Read by AttackContinuity's handover. (Raid releases through
        // ImminentResponseArmyIds, which also lists Regroup movers; an Attack army called home
        // already walks there on its Hard RecoveryReturn, so only an intercept takes it over.)
        internal static bool CanDefeatBeforeThreatWith(WorldSnapshot snap, AssetThreatSnapshot t, int armyId)
        {
            if (snap?.Self?.Armies == null)
                return false;
            var others = new HashSet<int>(snap.Self.Armies.Where(a => a != null && a.ArmyId != armyId)
                .Select(a => a.ArmyId));
            return CanDefeatBeforeThreat(snap, t, others, out int defeater) && defeater == armyId;
        }

        private static int FindDefeater(WorldSnapshot snap, AssetThreatSnapshot t, EnemyContactSnapshot c,
            ISet<int> excluded)
        {
            IReadOnlyList<WorthIt.DefendingArmy> opposition = KnownOpposition.OfEnemyArmy(snap, c.Army.ArmyId);
            if (opposition == null)
                return -1;
            var target = new ActiveDefenceMissionTarget { LastKnownHex = c.Position.Value,
                EnemyEta = t.EnemyEta, EnemyArmyId = c.Army.ArmyId };
            foreach (ArmySnapshot a in GroundCombatActorEligibility.EligibleArmies(snap, excluded,
                    requireMovementNow: false))
            {
                // The last defender of another threatened stronghold cannot leave for it.
                if (IsPinnedStrongholdDefender(snap, a, excluded, ignoreEnemyArmyId: c.Army.ArmyId))
                    continue;
                if (!CanInterceptPlanBeforeThreat(snap, target, GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(
                        snap, opposition, a.ArmyId, GroundCombatAdmissionPolicy.FreshStartWinChanceGate)))
                    continue;
                return a.ArmyId;
            }
            return -1;
        }

        // 2026-10-16 (user decision) — the last defender of a threatened stronghold may leave for a
        // DECISIVE attack (AttackForceReadiness.IsOpportunity) when the stronghold stays covered:
        // another own body remains on the hex (a scout counts), an own free ground army reaches it
        // this turn, or the hand holds a unit the stock can pay to place into the garrison now.
        // Without any cover the defender stays (IsPinnedStrongholdDefender).
        internal static bool StrongholdStaysCovered(WorldSnapshot snap, ArmySnapshot leaving,
            ISet<int> unavailable = null)
        {
            if (leaving == null || snap?.Self?.Armies == null)
                return false;
            HexCoord hex = leaving.Hex;
            foreach (ArmySnapshot a in snap.Self.Armies)
            {
                if (a == null || a.ArmyId == leaving.ArmyId || a.IsAir || a.IsAirfield || a.IsPrison
                    || a.MemberCount <= 0)
                    continue;
                // An army another operation holds may leave too; a garrison stays.
                if (!a.IsGarrison && unavailable != null && unavailable.Contains(a.ArmyId))
                    continue;
                if (a.Hex.Equals(hex))
                    return true;
                // "Reaches it this turn" by the real route price (terrain, storm exits), the
                // geometric distance only as the cheap lower bound that rules it out first.
                if (!a.IsGarrison && a.CurrentMovement > 0
                    && HexGridMath.Distance(a.Hex, hex) <= a.CurrentMovement
                    && AiV2Util.TravelCost(snap, a, hex, terrainOnly: true) <= a.CurrentMovement)
                    return true;
            }
            // The bank's spendable view (reservations excluded), never the raw stockpile.
            if (snap.Economy == null)
                return false;
            ResourceBundle stock = snap.Economy.SpendableStockpile;
            return snap.Self.Hand != null && snap.Self.Hand.Any(c =>
                c?.Definition != null && c.Definition.cardType == Game.Cards.CardType.Unit
                && ResourceBundle.All.All(t => (c.EffectivePlayResourceCost?.Get(t) ?? 0) <= stock.Get(t)));
        }

        // Reserve only actors in a concrete imminent response, not every nearby army.
        // This is an admission exclusion for optional raids, never an actor lease or spend.
        internal static HashSet<int> ImminentResponseArmyIds(WorldSnapshot snap, ISet<int> committed,
            IEnumerable<MissionIntent> intents = null)
        {
            var required = new HashSet<int>();
            foreach (ActiveDefenceObjective objective in Enumerate(snap))
            {
                if (!objective.Target.EnemyEta.HasValue
                    || objective.Target.EnemyEta.Value > AiConfigV2.activeDefenceWithdrawMaxEnemyEta)
                    continue;
                var response = AssessResponse(snap, objective, committed, null, null, intents);
                if (response?.Kind == ActiveDefenceResponseKind.Intercept && response.Plan != null)
                {
                    required.Add(response.Plan.BaseArmyId);
                    required.UnionWith(response.Plan.MergeArmyIds);
                    required.UnionWith(response.Plan.Transfers.Select(t => t.DonorArmyId));
                }
                else if (response != null && response.RegroupHex.HasValue)
                    required.UnionWith(response.ReinforcementArmyIds.Where(response.LeadsToAsset));
            }
            return required;
        }
    }
}
