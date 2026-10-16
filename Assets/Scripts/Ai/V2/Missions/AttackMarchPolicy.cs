using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    internal enum AttackMarchVerdict { Continue, Withdraw, DefendHome }

    internal readonly struct AttackMarchAssessment
    {
        internal readonly AttackMarchVerdict Verdict;
        internal readonly float WinChance;
        internal readonly bool Decisive;
        internal readonly HexCoord? HomeHex;
        internal readonly string Reason;

        internal AttackMarchAssessment(AttackMarchVerdict verdict, float win, bool decisive,
            HexCoord? home, string reason)
        {
            Verdict = verdict;
            WinChance = win;
            Decisive = decisive;
            HomeHex = home;
            Reason = reason;
        }
    }

    // ===========================================================================================
    //  2026-10-16 (user decision) — the strategic verdict for an Attack army on its way to the main
    //  target, re-read every turn on the facts now known. Snapshot-pure; Continuity applies it.
    //    1. Own stronghold in danger: an honest hostile army threatens an own Citadel / Base that
    //       cannot hold without this army, this army gets there no later than the enemy and its
    //       return makes it hold -> DefendHome. The one exception: a decisive blow at the enemy's
    //       Citadel that lands no later than the enemy reaches ours (taking the Citadel ends that
    //       player) -> Continue.
    //    2. Unknown defenders (location only) give no estimate -> Continue.
    //    3. Win chance at or above the abort floor (attackLocalMinWinChance) -> Continue: air
    //       support, reinforcement on the route and equipment keep improving the fight meanwhile.
    //    4. Below the floor -> Withdraw (Continuity first tries an existing support that meets the
    //       army ahead; the start gate attackStartMinWinChance keeps it from restarting on the
    //       same facts).
    // ===========================================================================================
    internal static class AttackMarchPolicy
    {
        internal static float AbortFloor => GroundCombatAdmissionPolicy.AttackLocalWinChanceGate;

        internal static AttackMarchAssessment Assess(WorldSnapshot snap, AttackTargetRef target,
            ArmySnapshot primary)
        {
            if (snap?.Self == null || primary == null)
                return new AttackMarchAssessment(AttackMarchVerdict.Continue, 0f, false, null, "no_primary");
            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, target.Hex);
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, null, target.Hex);
            GroundCombatFeasibility.Clears(primary.Members ?? System.Array.Empty<WorthIt.DefenderProfile>(),
                primary.Commander, opposition, 0f, hexBonus, out float win, out _, requireCoverage: false);
            bool known = opposition != null && opposition.Any(a => a.Units.Any(u => u.IsGroundCombatant));
            bool decisive = AttackForceReadiness.IsOpportunity(win, opposition);

            HexCoord? home = StrongholdNeedingArmy(snap, primary, out int enemyEta);
            if (home.HasValue)
            {
                bool mateFirst = decisive && target.Kind == AttackTargetKind.Citadel
                    && EtaTurns(primary, target.Hex) <= enemyEta;
                if (!mateFirst)
                    return new AttackMarchAssessment(AttackMarchVerdict.DefendHome, win, decisive, home,
                        $"own stronghold ({home.Value.Q},{home.Value.R}) falls without this army (enemy eta {enemyEta})");
            }
            if (!known)
                return new AttackMarchAssessment(AttackMarchVerdict.Continue, win, false, null, "defenders_unknown");
            if (win >= AbortFloor)
                return new AttackMarchAssessment(AttackMarchVerdict.Continue, win, decisive, null,
                    decisive ? "decisive" : "contested");
            return new AttackMarchAssessment(AttackMarchVerdict.Withdraw, win, false, null,
                $"win {win:0.00} < abort floor {AbortFloor:0.00}");
        }

        // The own Citadel / Base that an honest hostile army will take unless `army` returns: it
        // does not hold without the army (activeDefenceHoldWinChance), the army reaches it no
        // later than the enemy, and with the army it holds. The Citadel first, then the nearest
        // enemy. Null when no stronghold needs this army.
        internal static HexCoord? StrongholdNeedingArmy(WorldSnapshot snap, ArmySnapshot army,
            out int enemyEta)
        {
            enemyEta = int.MaxValue;
            if (snap?.Self?.BaseHexes == null || army == null)
                return null;
            HexCoord? best = null;
            bool bestCitadel = false;
            var without = new HashSet<int> { army.ArmyId };
            foreach (AssetThreatSnapshot t in snap.Threat?.Threats ?? Enumerable.Empty<AssetThreatSnapshot>())
            {
                if (!ActiveDefenceObjectiveEvaluator.IsHonestPositionedHostile(t) || !t.CanDamage
                    || !t.EnemyEta.HasValue || !snap.Self.BaseHexes.Contains(t.Asset.Hex)
                    || (t.Asset.Kind != AssetKind.Citadel && t.Asset.Kind != AssetKind.Base))
                    continue;
                int eta = t.EnemyEta.Value;
                if (EtaTurns(army, t.Asset.Hex) > eta)
                    continue;
                IReadOnlyList<WorthIt.DefendingArmy> enemy =
                    ActiveDefenceObjectiveEvaluator.Opposition(snap, t.Contact.Army.ArmyId);
                if (enemy == null)
                    continue;
                var objective = new ActiveDefenceObjective { Target = new ActiveDefenceMissionTarget
                    { ProtectedAssetHex = t.Asset.Hex, ProtectedAssetKind = t.Asset.Kind } };
                if (ActiveDefenceObjectiveEvaluator.HoldChanceAtAsset(snap, objective, null, null, enemy,
                        without) >= AiConfigV2.activeDefenceHoldWinChance)
                    continue;
                if (ActiveDefenceObjectiveEvaluator.HoldChanceAtAsset(snap, objective, t.Asset.Hex,
                        new[] { army }, enemy) < AiConfigV2.activeDefenceHoldWinChance)
                    continue;
                bool citadel = t.Asset.Kind == AssetKind.Citadel;
                if (best == null || (citadel && !bestCitadel) || (citadel == bestCitadel && eta < enemyEta))
                {
                    best = t.Asset.Hex;
                    bestCitadel = citadel;
                    enemyEta = eta;
                }
            }
            return best;
        }

        private static int EtaTurns(ArmySnapshot army, HexCoord to) =>
            AiV2Util.CeilDiv(HexGridMath.Distance(army.Hex, to), Mathf.Max(1, army.MaxMovement));
    }
}
