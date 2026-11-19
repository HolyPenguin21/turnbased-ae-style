using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  FOUNDING SITE DEFENCE (2026-11-19, project owner's decision — match Tessek vs Draven:
    //  Tessek founded Base (-2,0) on T31 two hexes from Draven's Base (0,-1), garrison 63-72,
    //  with an escort of 7.8 after the builder had left its Hazard Pioneer at home).
    //  A new Base must be holdable on the turns after it is founded:
    //    threat  = every known hostile ground force reaching the site within
    //              activeDefenceWithdrawMaxEnemyEta turns x aggDefenceConfidenceMargin, enemy
    //              garrisons included (AiArmyRoles.FoundingSiteThreat);
    //    defence = the founding party's bodies + the strongest hand unit cards the new garrison
    //              can hold (they are played into it the turn it stands) + the own field armies on
    //              other own Bases that reach the site before the enemy (AiArmyRoles.MobileReserveCredit,
    //              the standing fist — "or waits for the fist").
    //  The shortfall share is the site's HexThreatRisk (DemandLayer.BaseCandidate), and a threatened
    //  founding never lightens its builder at home (EconomyBuilderSelection: the whole roster goes).
    // ===========================================================================================
    internal static class FoundingSiteDefence
    {
        // A new Base's garrison holds this many bodies before a hero commands it (ArmyData rule).
        private const int NewGarrisonSlots = 4;

        internal readonly struct Assessment
        {
            internal readonly float Threat, Escort, Hand, Reserve;
            internal readonly int EnemyEta;
            internal Assessment(float threat, float escort, float hand, float reserve, int enemyEta)
            { Threat = threat; Escort = escort; Hand = hand; Reserve = reserve; EnemyEta = enemyEta; }
            internal float Defence => Escort + Hand + Reserve;
            internal float ShortfallShare => Threat <= AiConfigV2.allocatorSliceEpsilon ? 0f
                : Mathf.Clamp01((Threat - Defence) / Threat);
            internal string Describe() =>
                $"siteThreat={Threat:0.#}(eta {(EnemyEta == int.MaxValue ? "-" : EnemyEta.ToString())}) "
                + $"defence={Defence:0.#}[escort {Escort:0.#} hand {Hand:0.#} reserve {Reserve:0.#}]";
        }

        internal static bool IsThreatened(WorldSnapshot snap, HexCoord site) =>
            snap?.Observer != null && AiArmyRoles.FoundingSiteThreat(snap.Observer, site, out _)
                > AiConfigV2.allocatorSliceEpsilon;

        internal static Assessment Assess(WorldSnapshot snap, HexCoord site, float escortPower, int escortBodies)
        {
            float threat = AiArmyRoles.FoundingSiteThreat(snap?.Observer, site, out int eta);
            if (threat <= AiConfigV2.allocatorSliceEpsilon)
                return new Assessment(0f, escortPower, 0f, 0f, eta);
            float hand = HandGarrisonPower(snap, Mathf.Max(0, NewGarrisonSlots - escortBodies));
            float reserve = AiArmyRoles.MobileReserveCredit(snap.Observer, site, eta);
            return new Assessment(threat, escortPower, hand, reserve, eta);
        }

        // The strongest unit cards in hand the new garrison has room for (StrikePool, Hand source).
        internal static float HandGarrisonPower(WorldSnapshot snap, int slots)
        {
            IReadOnlyList<StrikeRosterCandidate> pool = snap?.Self?.StrikePool;
            if (pool == null || slots <= 0)
                return 0f;
            List<AiPower.PowerUnit> best = pool
                .Where(c => c.Slot.Source == ForceSource.Hand && !c.Slot.IsHero)
                .Select(c => c.Unit)
                .OrderByDescending(u => u.BasePower)
                .Take(slots)
                .ToList();
            return best.Count == 0 ? 0f : AiPower.EffectiveArmyPower(best);
        }

        // The ground bodies the chosen builder founds the Base with: the bodies it keeps for the
        // trip (RetainedIndices), the one a garrison extraction adds, or its whole roster.
        internal static (float Power, int Bodies) EscortOf(EconomyBuilderSelection.EconomyBuilderChoice builder)
        {
            ArmySnapshot army = builder?.Army;
            if (army?.Members == null)
                return (0f, 0);
            IEnumerable<int> indices = builder.Route.RequiresGarrisonExtraction ? builder.AddedIndices
                : builder.RetainedIndices != null && builder.RetainedIndices.Count > 0 ? builder.RetainedIndices
                : Enumerable.Range(0, army.Members.Count);
            List<Game.Combat.WorthIt.DefenderProfile> bodies = indices
                .Where(i => i >= 0 && i < army.Members.Count && AiArmyRoles.IsGroundBattleBody(army.Members[i]))
                .Select(i => army.Members[i]).ToList();
            return (AiPower.EffectiveArmyPowerFromProfiles(bodies), bodies.Count);
        }
    }
}
