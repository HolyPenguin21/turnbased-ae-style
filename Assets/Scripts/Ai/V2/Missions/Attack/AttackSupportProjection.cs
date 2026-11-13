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
    //      there or arrives no later than the fist, else (2026-11-07) a free fielded carrier that
    //      does so for a march of at least a turn (FreeCarrier, the free-wing rule) — the
    //      structural part of the bonus goes (WorthIt.DefendingArmy.BuildingDefenseBonus, the
    //      rule DisruptionRules applies).
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
            // A free carrier matters only where a structure bonus exists to be removed.
            bool freeCarrier = known.Any(a => a.BuildingDefenseBonus > 0f)
                && ScoutMoverSelector.HasStructuralCandidate(snap, DisruptionTarget(targetHex));
            if (!hasAircraft && !carrierLeg && !freeCarrier)
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
            if (!carrier.HasValue && freeCarrier)
                carrier = FreeCarrier(snap, targetHex, eta);
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
            return opposition.Select(a => a.With(a.Units, a.Commander,
                a.DefenseBonusOverride.HasValue
                    ? System.Math.Max(0f, a.DefenseBonusOverride.Value - System.Math.Max(0f, a.BuildingDefenseBonus))
                    : (float?)null,
                0f)).ToList();
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
                if (carrier.Hex.Equals(targetHex)
                    || (CarrierCanEnter(snap, targetHex, armyEta) && ArmyEta(snap, carrier, targetHex) <= armyEta))
                    return carrier.ArmyId;
            }
            return null;
        }

        // 2026-11-07 — a carrier still to arrive counts only where a hidden scout may stand: the
        // site is not blocked for a stealth-capable scout and no known detector covers it — the
        // same honest rule the delivery itself and its terminal wait apply
        // (ReconAssignmentPlanner.CanDeliverDisruptionThisTurn). A carrier already standing there
        // counts as it stands.
        // 2026-11-13 (owner, game rule) — a detector does not stop the delivery a fist able to
        // enter this very turn backs (DetectorIgnoredForDelivery). IsBlockedForScout(stealthCapable)
        // stays: it is only the hard blocks (neutral on the hex, impassable, off the map).
        private static bool CarrierCanEnter(WorldSnapshot snap, HexCoord targetHex, int armyEta) =>
            snap?.MapKnowledge?.IsBlockedForScout(targetHex, stealthCapable: true) != true
            && (ScoutRiskModel.DetectorRisk(snap, targetHex) < 1f
                || (armyEta == 0 && DetectorIgnoredForDelivery(snap, targetHex)));

        // THE detector exception (owner, 2026-11-13): a hidden Disruption carrier may enter an
        // enemy Base hex a detector covers — a fully hidden army starts no contact and holds nobody
        // even when detected (BattleInitiator.CanInitiateContact / IsHeldByContact) — and the fist
        // enters after it in the same turn, under Disruption. So for a DisruptStructure delivery to
        // a LIVE Attack's target whose fist reaches that target this turn, detector risk on the
        // target and on the delivery's own last leg is not counted. Explore/Refresh/Hunt lanes and
        // a delivery without a ready fist keep the detector rules.
        internal static bool DetectorIgnoredForDelivery(WorldSnapshot snap, HexCoord targetHex)
        {
            MissionIntent assault = ReconObjectiveEvaluator.LiveAssaultAt(snap, targetHex);
            // A Gather is still assembling: its fist does not enter this turn.
            if (assault?.Attack == null || assault.Attack.Phase == AttackMissionPhase.Gather)
                return false;
            int? fistId = assault.Attack.PrimaryArmyId ?? assault.PreferredMoverArmyId;
            ArmySnapshot fist = fistId.HasValue
                ? snap.Self?.Armies?.FirstOrDefault(a => a != null && a.ArmyId == fistId.Value) : null;
            return fist != null && ArmyEta(snap, fist, targetHex) == 0;
        }

        // 2026-11-07 (owner, playtest Ysolde T26) — the free carrier, counted the way CountedAir
        // counts a free wing: only for a march of at least a turn, and only an own hidden (or
        // stealth-capable) ground Disruption carrier that no operation holds — or that only a
        // displaceable Explore/Refresh lane holds (ReconMissionPlanner.DisplaceableReconRole) —
        // and that reaches the target no later than the fist. Recon then binds it: a live Attack
        // whose fist has no carrier makes the DisruptStructure objective
        // (ReconObjectiveEvaluator.AssaultNeedsDisruption) with Attack's priority, and the leg it
        // opens is what CountedCarrier counts from then on; the terminal step waits for it
        // (AttackObjectiveEvaluator.HasAssaultDisruption). Without it the planner saw the structure bonus
        // and swapped a combat body out of the 8-slot fist for a weak in-fist carrier (Crawler ->
        // Scout) while two free stealth Scouts stood by.
        internal static int? FreeCarrier(WorldSnapshot snap, HexCoord targetHex, int armyEta)
        {
            if (armyEta == int.MaxValue || armyEta < 1 || snap?.Self?.Armies == null)
                return null;
            bool canEnter = CarrierCanEnter(snap, targetHex, armyEta);
            List<MissionIntent> intents = (snap.AllIntents ?? System.Array.Empty<MissionIntent>())
                .Where(i => i != null && i.Status == IntentStatus.Active).ToList();
            foreach (ArmySnapshot a in ScoutMoverSelector.StructuralCandidates(snap, DisruptionTarget(targetHex))
                .OrderBy(a => a.ArmyId))
            {
                bool held = intents.Any(i => i.PreferredMoverArmyId == a.ArmyId);
                if (held && ReconMissionPlanner.DisplaceableReconRole(intents, a.ArmyId) == null)
                    continue;
                if (a.Hex.Equals(targetHex) || (canEnter && ArmyEta(snap, a, targetHex) <= armyEta))
                    return a.ArmyId;
            }
            return null;
        }

        private static ScoutMissionTarget DisruptionTarget(HexCoord hex) => new ScoutMissionTarget
        {
            Kind = ScoutTargetKind.DisruptStructure, FocusHex = hex, Stealth = StealthRequirement.Required,
        };

        internal static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
