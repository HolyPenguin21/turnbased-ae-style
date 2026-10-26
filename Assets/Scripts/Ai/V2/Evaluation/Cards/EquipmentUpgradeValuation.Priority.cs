using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    internal static partial class EquipmentUpgradeValuation
    {
        // Transient data of the existing valuation scope, never an actor claim or spending hold.
        internal sealed class RecipientPriorityContext
        {
            internal readonly Dictionary<MissionIntent, HashSet<object>> Preparation = new();
            internal HashSet<object> Peak;
            internal bool? MissileProduction;
        }

        private sealed class RosterRecipient
        {
            internal object Identity;
            internal string Key;
            internal bool Hero;
            internal int Rank, Id;
            internal float Power;
        }

        private static bool OwnsArmy(MissionIntent intent, ArmyData army) => army != null
            && (intent.PreferredMoverArmyId == army.Id
                || GroundCombatLegs.HeldGroundSupportArmyIds(intent).Contains(army.Id));

        private static bool DefenceDuty(WorldSnapshot snap, ArmyData army, UnitData unit)
        {
            if (army == null || unit == null) return false;
            if (army.IsGarrison && !AiArmyRoles.CanSpareGarrisonMember(army.Owner, army, unit)) return true;
            return (snap?.Self?.BaseHexes?.Contains(army.Hex) == true)
                && (snap.Threat?.Threats ?? Array.Empty<AssetThreatSnapshot>()).Any(t =>
                    ActiveDefenceObjectiveEvaluator.IsHonestPositionedHostile(t) && t.CanDamage
                    && t.Asset.Hex.Equals(army.Hex) && t.EnemyEta.HasValue
                    && t.EnemyEta.Value <= AiConfigV2.activeDefenceWithdrawMaxEnemyEta);
        }

        private static HashSet<object> SelectRosterRecipients(WorldSnapshot snap,
            IReadOnlyList<StrikeRosterSlot> roster, MissionIntent operation)
        {
            var selected = new HashSet<object>();
            if (snap?.Observer == null || roster == null || roster.Count == 0) return selected;
            var intents = WorldSnapshot.IntentsOf(snap).Where(i => i?.Status == IntentStatus.Active).ToList();
            var candidates = new List<RosterRecipient>();
            foreach (ArmyData army in OwnWorld.Of(snap.Observer).Armies)
            {
                MissionIntent assignedOperation = intents.FirstOrDefault(i => OwnsArmy(i, army));
                if (army.IsPrison || army.IsAirfield || army.IsAirArmy
                    || operation != null && intents.Any(i => i != operation && OwnsArmy(i, army))
                    || operation == null && assignedOperation != null && assignedOperation.GroundCombat == null) continue;
                foreach (UnitData unit in army.Members)
                {
                    if (unit == null || unit.HitPointsCurrent <= 0 || unit.IsPrisoner || unit.IsAviation
                        || unit.IsSummoned || (!unit.IsHero && !AiArmyRoles.IsGroundBattleBody(unit))) continue;
                    // An already assigned ground participant consumes its physical peak-roster
                    // copy too. Excluding it would buy a second full set for the same preparation.
                    bool assigned = operation != null ? OwnsArmy(operation, army) : assignedOperation != null;
                    if (!assigned && (AiArmyRoles.IsFacilityOperator(snap.Observer, army.Hex, unit)
                        || DefenceDuty(snap, army, unit))) continue;
                    candidates.Add(new RosterRecipient { Identity = unit, Key = StrikeRoster.UnitKey(unit),
                        Hero = unit.IsHero, Power = unit.IsHero ? unit.CommandRating : AiPower.UnitPower(unit),
                        Rank = operation?.Attack?.PrimaryArmyId == army.Id || operation == null && assigned ? 0
                            : assigned ? 1 : army.IsGarrison ? 3 : 2,
                        Id = unit.RuntimeId });
                }
            }
            foreach (CardData card in snap.Self?.Hand ?? Array.Empty<CardData>())
            {
                var def = card?.Definition;
                if (def == null || def.isAviation || (def.cardType != CardType.Unit && def.cardType != CardType.Hero)) continue;
                candidates.Add(new RosterRecipient { Identity = card, Key = StrikeRoster.CardKey(def),
                    Hero = def.cardType == CardType.Hero, Power = def.cardType == CardType.Hero
                        ? EquipmentSystem.Project(card).Stats[EquipmentStat.CommandRating] : AiPower.ToPowerUnit(card).BasePower,
                    Rank = 4, Id = card.RuntimeId });
            }
            foreach (var group in roster.GroupBy(r => (r.Key, r.IsHero)))
                foreach (var candidate in candidates.Where(c => c.Key == group.Key.Key && c.Hero == group.Key.IsHero)
                    .OrderBy(c => c.Rank).ThenByDescending(c => c.Power).ThenBy(c => c.Id).Take(group.Count()))
                    selected.Add(candidate.Identity);
            return selected;
        }

        private static bool SelectedForPreparation(WorldSnapshot snap, MissionIntent intent, object recipient)
        {
            if (recipient == null || intent?.Attack?.Preparation != true || intent.Status != IntentStatus.Active)
                return false;
            var context = EquipmentTargetMemo.Priority(snap);
            if (!context.Preparation.TryGetValue(intent, out var selected))
                context.Preparation[intent] = selected = SelectRosterRecipients(snap, intent.Attack.TargetRoster, intent);
            return selected.Contains(recipient);
        }

        internal static bool IsRequiredEquipmentRecipient(WorldSnapshot snap, CardData card, UnitData unit,
            MaterializationPlan deployment = null)
        {
            if (snap?.Observer == null) return false;
            if (EquipmentPurpose(snap, card, unit) != null) return true;
            ArmyData army = unit == null ? deployment?.Deploy.Army : OwnWorld.Of(snap.Observer).ArmyContaining(unit);
            if (unit != null && army != null && (DefenceDuty(snap, army, unit)
                || AiArmyRoles.IsFacilityOperator(snap.Observer, army.Hex, unit))) return true;
            if (army != null && WorldSnapshot.IntentsOf(snap).Any(i => i?.Status == IntentStatus.Active
                && OwnsArmy(i, army))) return true;
            if (!AttackForceReadiness.MobilizationOpen(snap.Self)) return false;
            var context = EquipmentTargetMemo.Priority(snap);
            context.Peak ??= SelectRosterRecipients(snap, snap.Self.StrikeRoster, null);
            return context.Peak.Contains((object)unit ?? card);
        }

        internal static EquipmentDelta CalibrateRecipientGain(EquipmentDelta delta, bool required, bool missile)
        {
            if (required || !missile || delta.Total <= 0f) return delta;
            float multiplier = AiConfigV2.equipmentReserveMissileCompetitionMultiplier;
            return new EquipmentDelta(delta.Combat > 0 ? delta.Combat * multiplier : delta.Combat,
                delta.Tactical > 0 ? delta.Tactical * multiplier : delta.Tactical,
                () => delta.Detail + $" recipient=reserve missile-production-competes x{multiplier:0.##}");
        }

        private static EquipmentDelta PrioritizeRecipient(EquipmentDelta delta, WorldSnapshot snap,
            CardData card, UnitData unit, MaterializationPlan deployment = null, bool requiredRecipient = false)
        {
            if (requiredRecipient || snap == null || delta.Total <= 0f || IsRequiredEquipmentRecipient(snap, card, unit, deployment)) return delta;
            var context = EquipmentTargetMemo.Priority(snap);
            context.MissileProduction ??= NonCombatCardPlayer.HasExecutableMissileProduction(snap,
                reservation: EquipmentTargetMemo.Reservation);
            return CalibrateRecipientGain(delta, false, context.MissileProduction.Value);
        }

        // Raw dependencies only: no path search, simulation, spending or actor mutation.
        internal static string RecipientPriorityFacts(WorldSnapshot snap) =>
            "generation=" + (EquipmentTargetMemo.Reservation is MaterializationReservation reservation
                ? reservation.GenerationAttemptsUsed + ":" + string.Join(",", reservation.TriedGeneratorCards.OrderBy(k => k, StringComparer.Ordinal))
                : StrategicTempoBudget.GenerationUsed(snap?.Observer, snap?.TurnNumber ?? 0) + ":unreserved")
            + "|window=" + DevelopmentInvestmentGate.OpenMask(snap?.Observer, snap?.TurnNumber ?? 0)
            + "|" + string.Join("|",
            WorldSnapshot.IntentsOf(snap).Where(i => i != null).OrderBy(i => i.IntentKey).Select(i =>
                $"{i.IntentKey}:{i.Status}:{i.PreferredMoverArmyId}:{i.Attack?.PrimaryArmyId}:{i.Attack?.Preparation}:"
                + string.Join(",", GroundCombatLegs.HeldGroundSupportArmyIds(i).OrderBy(id => id)) + ":"
                + string.Join(",", i.Attack?.TargetRoster?.Select(r => $"{r.Key}/{r.IsHero}") ?? Array.Empty<string>())))
            + "|defence=" + string.Join(";", (snap?.Threat?.Threats ?? Array.Empty<AssetThreatSnapshot>())
                .Select(t => $"{t.Asset?.Hex}:{t.Asset?.Kind}:{t.CanDamage}:{t.EnemyEta}:{t.Contact?.Knowledge}:"
                    + $"{t.Contact?.Army?.ArmyId}:{t.Contact?.Position}:{t.Contact?.Army?.Owner?.IsNeutral}"));
    }
}
