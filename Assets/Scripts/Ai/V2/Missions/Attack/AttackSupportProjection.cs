using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Aviation;
using Game.Combat;
using Game.HexGrid;

namespace Game.Ai.V2
{
    // What an Attack march will meet at its target, as the decisions about that march read it.
    internal readonly struct AttackSupportForecast
    {
        // The known package and hex bonus as they stand now.
        internal readonly IReadOnlyList<WorthIt.DefendingArmy> BaseOpposition;
        internal readonly float BaseHexBonus;
        // The same after the committed support has acted (== Base* when none is counted).
        internal readonly IReadOnlyList<WorthIt.DefendingArmy> Opposition;
        internal readonly float HexBonus;
        // The counted air support: the bound wing / free wing / airfield launch, and the turns to
        // its first strike (0 = this turn). Null when no air support is counted.
        internal readonly int? AirSourceArmyId;
        internal readonly int AirFirstStrikeEta;
        internal readonly bool AirBound;
        // The counted Disruption carrier (an own army on an active DisruptStructure leg).
        internal readonly int? DisruptCarrierArmyId;

        internal AttackSupportForecast(IReadOnlyList<WorthIt.DefendingArmy> baseOpposition, float baseHexBonus,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus, int? airSourceArmyId,
            int airFirstStrikeEta, bool airBound, int? disruptCarrierArmyId)
        {
            BaseOpposition = baseOpposition;
            BaseHexBonus = baseHexBonus;
            Opposition = opposition;
            HexBonus = hexBonus;
            AirSourceArmyId = airSourceArmyId;
            AirFirstStrikeEta = airFirstStrikeEta;
            AirBound = airBound;
            DisruptCarrierArmyId = disruptCarrierArmyId;
        }

        internal bool CountsSupport => AirSourceArmyId.HasValue || DisruptCarrierArmyId.HasValue;

        internal string Describe() =>
            !CountsSupport ? "support=none"
                : "support=" + string.Join("+", new[]
                {
                    AirSourceArmyId.HasValue
                        ? $"air#{AirSourceArmyId}{(AirBound ? "(bound)" : "")}@eta{AirFirstStrikeEta}" : null,
                    DisruptCarrierArmyId.HasValue ? $"disruption#{DisruptCarrierArmyId}" : null,
                }.Where(x => x != null));
    }

    // ===========================================================================================
    //  2026-11-02 (owner decision) — THE ONE "what will the march meet" of an Attack.
    //
    //  The start, the march verdict (AttackMarchPolicy.Assess), the Gather->Assault clearance
    //  (AttackContinuity) and the mission planner's assembly all price the fight against this one
    //  forecast instead of the bare known package. Otherwise a wing bound to the operation flew
    //  while the fist, reading the same unchanged facts, withdrew; and Continuity (no terrain)
    //  and the planner (terrain) disagreed on whether the same host clears the same site.
    //
    //  Only support that physically exists and acts before the fist arrives counts — never a
    //  missile not yet produced or a carrier not yet fielded:
    //    · air: the operation's bound wing whose first strike lands no later than one turn after
    //      the fist could arrive (the terminal step waits that one turn:
    //      AttackMarchPolicy.WaitForAirStrike); else the best free formed wing / airfield launch
    //      that strikes no later than a fist still at least a turn away (it is bound only once
    //      the operation starts). GroundCombatAirSupport.Options — its estimate and post-strike
    //      package, not a second model;
    //    · Disruption: an own carrier on an active DisruptStructure leg at the target that stands
    //      there or arrives no later than the fist — the structural part of the bonus goes
    //      (WorthIt.DefendingArmy.BuildingDefenseBonus, the rule DisruptionRules applies).
    //  Losing the support changes the forecast, and the ordinary withdraw rule then applies.
    // ===========================================================================================
    internal static class AttackSupportProjection
    {
        // `armyEta` — the fist's turns to the target (0 = reachable this turn); int.MaxValue when
        // unknown, which counts no air support that would wait for it.
        internal static AttackSupportForecast For(WorldSnapshot snap, HexCoord targetHex, int armyEta) =>
            For(snap, targetHex, () => armyEta);

        // `armyEta` is asked only when some support could be counted (it may cost a route search).
        internal static AttackSupportForecast For(WorldSnapshot snap, HexCoord targetHex, System.Func<int> armyEta)
        {
            List<WorthIt.DefendingArmy> known = KnownOpposition.OnHex(snap, targetHex);
            float hexBonus = AiMapMemory.KnownHexDefenseBonus(snap?.Observer, snap?.Map, targetHex);
            if (snap?.Self?.Armies == null || known.Count == 0)
                return new AttackSupportForecast(known, hexBonus, known, hexBonus, null, 0, false, null);

            IReadOnlyList<WorthIt.DefendingArmy> opposition = known;
            int? airSource = null;
            int airEta = 0;
            bool airBound = false;
            bool hasAircraft = snap.Self.Armies.Any(a => a != null && (a.IsAir || a.IsAirfield) && a.MemberCount > 0);
            bool carrierLeg = (snap.AllIntents ?? System.Array.Empty<MissionIntent>()).Any(i =>
                i?.Status == IntentStatus.Active && i.Scout?.Kind == ScoutTargetKind.DisruptStructure
                && i.Scout.FocusHex.Equals(targetHex));
            if (!hasAircraft && !carrierLeg)
                return new AttackSupportForecast(known, hexBonus, known, hexBonus, null, 0, false, null);
            int eta = armyEta?.Invoke() ?? int.MaxValue;
            AirSupportOption? air = hasAircraft ? CountedAir(snap, targetHex, known, eta, out airBound) : null;
            if (air.HasValue)
            {
                opposition = air.Value.OppositionAfter;
                airSource = air.Value.WingArmyId;
                airEta = air.Value.FirstStrikeEta;
            }

            float projectedBonus = hexBonus;
            int? carrier = carrierLeg ? CountedCarrier(snap, targetHex, eta) : null;
            if (carrier.HasValue)
                opposition = WithoutStructure(opposition, hexBonus, out projectedBonus);
            return new AttackSupportForecast(known, hexBonus, opposition, projectedBonus,
                airSource, airEta, airBound, carrier);
        }

        // The forecast for an army: its own turns to the target.
        internal static AttackSupportForecast For(WorldSnapshot snap, HexCoord targetHex, ArmySnapshot army) =>
            For(snap, targetHex, () => ArmyEta(snap, army, targetHex));

        // The soonest any free structural ground fist could arrive: a fresh plan's actor is not
        // chosen yet, and counting support against the fastest one never counts it for a slower one
        // that would arrive before it.
        internal static int FastestFistEta(WorldSnapshot snap, HexCoord targetHex, ISet<int> excluded = null)
        {
            int best = int.MaxValue;
            foreach (ArmySnapshot a in snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
                if (a != null && a.IsStructuralRaidActor && !a.IsAir && a.MaxMovement > 0
                    && (excluded == null || !excluded.Contains(a.ArmyId)))
                    best = System.Math.Min(best, ArmyEta(snap, a, targetHex));
            return best;
        }

        internal static int ArmyEta(WorldSnapshot snap, ArmySnapshot army, HexCoord targetHex) =>
            army == null ? int.MaxValue
                : ActiveDefenceObjectiveEvaluator.GroundArrivalEta(snap, army.Hex, targetHex,
                    army.CurrentMovement, army.MaxMovement, fastestByTurns: true);

        private static AirSupportOption? CountedAir(WorldSnapshot snap, HexCoord targetHex,
            IReadOnlyList<WorthIt.DefendingArmy> known, int armyEta, out bool bound)
        {
            bound = false;
            if (armyEta == int.MaxValue)
                return null;
            MissionIntent operation = (snap.AllIntents ?? System.Array.Empty<MissionIntent>())
                .FirstOrDefault(i => i?.Status == IntentStatus.Active && i.Attack != null
                    && i.Attack.Target.Hex.Equals(targetHex));
            int? boundWing = operation?.Attack?.AirSupportArmyId;
            List<AirSupportOption> options = GroundCombatAirSupport.Ranked(GroundCombatAirSupport.Options(
                snap, known, targetHex, AirStrikePolicy.Standard, null, 0f,
                boundWing.HasValue ? null : WingsHeldElsewhere(snap, operation), fixedWingArmyId: boundWing));
            // A bound wing may strike up to one turn after the fist could arrive: the terminal step
            // waits for it (AttackMarchPolicy.WaitForAirStrike). A free wing is bound only once the
            // operation has started (AttackContinuity.ResolveAttackAirSupport, after the first
            // step), so it counts only for a march of at least a turn that it reaches no later.
            bool operationWing = boundWing.HasValue;
            foreach (AirSupportOption o in options)
                if (o.RosterKnown && o.OppositionAfter != null
                    && o.ExpectedDamage > AiConfigV2.allocatorSliceEpsilon
                    && (operationWing ? o.FirstStrikeEta <= armyEta + 1
                        : armyEta >= 1 && o.FirstStrikeEta <= armyEta))
                {
                    bound = boundWing.HasValue;
                    return o;
                }
            return null;
        }

        // Wings another operation already bound: not this march's support.
        private static HashSet<int> WingsHeldElsewhere(WorldSnapshot snap, MissionIntent self)
        {
            var held = new HashSet<int>();
            foreach (MissionIntent i in snap.AllIntents ?? System.Array.Empty<MissionIntent>())
            {
                if (i == null || i.Status != IntentStatus.Active || ReferenceEquals(i, self))
                    continue;
                if (i.Attack?.AirSupportArmyId != null) held.Add(i.Attack.AirSupportArmyId.Value);
                if (i.Raid?.AirSupportArmyId != null) held.Add(i.Raid.AirSupportArmyId.Value);
                if (i.ActiveDefence?.Phase == ActiveDefencePhase.AirSupport && i.PreferredMoverArmyId.HasValue)
                    held.Add(i.PreferredMoverArmyId.Value);
            }
            return held;
        }

        // The package once a Disruption carrier stands on the hex: only the structural part of each
        // army's bonus goes (the part DisruptionRules.BuildingDefenseAtEncounterStart zeroes).
        internal static List<WorthIt.DefendingArmy> WithoutStructure(
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus, out float projectedHexBonus)
        {
            opposition = opposition ?? System.Array.Empty<WorthIt.DefendingArmy>();
            float structural = opposition.Select(a => System.Math.Max(0f, a.BuildingDefenseBonus))
                .DefaultIfEmpty(0f).Max();
            projectedHexBonus = System.Math.Max(0f, hexBonus - structural);
            return opposition.Select(a => new WorthIt.DefendingArmy(a.Units, a.Commander,
                a.DefenseBonusOverride.HasValue
                    ? System.Math.Max(0f, a.DefenseBonusOverride.Value - System.Math.Max(0f, a.BuildingDefenseBonus))
                    : (float?)null,
                a.ArmyId, 0f)).ToList();
        }

        // The own carrier on an active DisruptStructure leg at the target that stands there or
        // arrives no later than the fist (armyEta), or null.
        internal static int? CountedCarrier(WorldSnapshot snap, HexCoord targetHex, int armyEta)
        {
            foreach (MissionIntent i in snap.AllIntents ?? System.Array.Empty<MissionIntent>())
            {
                if (i?.Status != IntentStatus.Active || i.Scout?.Kind != ScoutTargetKind.DisruptStructure
                    || !i.Scout.FocusHex.Equals(targetHex) || !i.PreferredMoverArmyId.HasValue)
                    continue;
                ArmySnapshot carrier = snap.Self.Armies.FirstOrDefault(a => a != null
                    && a.ArmyId == i.PreferredMoverArmyId.Value);
                if (carrier == null || !(DisruptionRules.HasCarrier(carrier.MembersWithHeroes)
                        || DisruptionRules.HasCarrier(carrier.Members)))
                    continue;
                if (carrier.Hex.Equals(targetHex) || ArmyEta(snap, carrier, targetHex) <= armyEta)
                    return carrier.ArmyId;
            }
            return null;
        }

        internal static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
