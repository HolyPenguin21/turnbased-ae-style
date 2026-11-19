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
    // Withdrawal — the safe retreat of an army a threat would destroy, and the bench hero's shelter.
    public static partial class ActiveDefenceObjectiveEvaluator
    {
        internal static bool NeedsSafeWithdrawal(WorldSnapshot snap, ArmySnapshot actor) =>
            WithdrawalThreats(snap, actor).Any();

        // The honest threats to this army itself (its Army asset) that make it withdraw.
        private static IEnumerable<AssetThreatSnapshot> WithdrawalThreats(WorldSnapshot snap,
            ArmySnapshot actor) =>
            actor == null ? Enumerable.Empty<AssetThreatSnapshot>()
                : (snap?.Threat?.Threats ?? Array.Empty<AssetThreatSnapshot>()).Where(t =>
                    IsHonestPositionedHostile(t) && t.Asset.Kind == AssetKind.Army
                    && t.Asset.Hex.Equals(actor.Hex) && t.CanDamage && t.EnemyEta.HasValue
                    && t.EnemyEta.Value <= AiConfigV2.activeDefenceWithdrawMaxEnemyEta
                    && t.AttackWinChance > 1f - AiConfigV2.activeDefenceHoldWinChance);

        // 2026-11-01 (owner decision) — a free lone hero is a bench (Housekeeping, 2026-10-15), and
        // under a known threat it shelters; Housekeeping can only put it into a garrison on its own
        // hex, and the Shortage withdrawal lists usable fighters only, so a bench standing off every
        // own Base had no owner (Rurik #27 Aldric Voss idle at (1,-2) T13-T20, Draven's fist one hex
        // away). Its objective is the threat to its own Army asset, the one NeedsSafeWithdrawal
        // reads; the leg is an ordinary SafeWithdrawal Return (Continuity and Provisioning re-check
        // NeedsSafeWithdrawal and SafeWithdrawalBase). Null: not a threatened off-base bench.
        internal static ActiveDefenceObjective BenchShelterObjective(WorldSnapshot snap, ArmySnapshot hero)
        {
            if (!GroundCombatActorEligibility.IsHeroAssemblyHost(hero)
                || snap?.Self?.BaseHexes?.Contains(hero.Hex) == true)
                return null;
            AssetThreatSnapshot t = WithdrawalThreats(snap, hero)
                .OrderBy(x => x.EnemyEta.Value).ThenByDescending(x => x.Severity)
                .ThenBy(x => x.Contact.Army.ArmyId).FirstOrDefault();
            if (t == null)
                return null;
            return new ActiveDefenceObjective
            {
                Target = new ActiveDefenceMissionTarget
                {
                    Phase = ActiveDefencePhase.Return,
                    EnemyArmyId = t.Contact.Army.ArmyId,
                    LastKnownHex = t.Contact.Position.Value,
                    LastObservedTurn = t.Contact.LastObservedTurn,
                    Confidence = t.Confidence,
                    ProtectedAssetHex = t.Asset.Hex,
                    ProtectedAssetKind = t.Asset.Kind,
                    ProtectedAssetValue = t.Asset.Value,
                    ThreatSeverity = t.Severity,
                    EstimatedEta = t.EnemyEta.Value,
                    EnemyEta = t.EnemyEta,
                },
                TaskScore = BuildActiveDefenceScore(snap, t),
            };
        }

        // Keep the global base ranking, constrained only for this lane to a reachable safe home.
        internal static HexCoord? SafeWithdrawalBase(WorldSnapshot snap, ArmySnapshot actor,
            ISet<int> committed, HexCoord? current = null)
        {
            HexCoord? preferred = current ?? AiReturnBasePolicy.SelectReturnBase(snap, snap.Observer, actor.ArmyId);
            foreach (HexCoord h in (snap.Self.BaseHexes ?? Array.Empty<HexCoord>())
                .OrderBy(h => preferred.HasValue && h.Equals(preferred.Value) ? 0 : 1)
                .ThenBy(h => ArrivalEta(snap, actor, h)).ThenBy(h => h.Q).ThenBy(h => h.R))
            {
                if (ArrivalEta(snap, actor, h) == int.MaxValue) continue;
                bool unsafeBase = (snap.Threat?.Threats ?? Array.Empty<AssetThreatSnapshot>()).Any(t =>
                    IsHonestPositionedHostile(t) && t.Asset.Hex.Equals(h) && t.CanDamage
                    && t.EnemyEta.HasValue && t.EnemyEta.Value <= AiConfigV2.activeDefenceWithdrawMaxEnemyEta
                    && HoldChanceAtAsset(snap, new ActiveDefenceObjective { Target = new ActiveDefenceMissionTarget
                        { ProtectedAssetHex = h, ProtectedAssetKind = t.Asset.Kind } }, h,
                        actor.Hex.Equals(h) || CanArriveBeforeThreat(snap, actor, h, t.EnemyEta, out _)
                            ? new[] { actor } : System.Array.Empty<ArmySnapshot>(),
                        KnownOpposition.OfEnemyArmy(snap, t.Contact.Army.ArmyId), committed)
                        < AiConfigV2.activeDefenceHoldWinChance);
                if (!unsafeBase) return h;
            }
            return null;
        }
    }
}
