using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // One counted wing of an Attack's air support: the bound wing / free wing / airfield launch,
    // the turns to its first strike (0 = this turn) and whether the operation already holds it.
    internal readonly struct CountedAirStrike
    {
        internal readonly int ArmyId;
        internal readonly int FirstStrikeEta;
        internal readonly bool Bound;

        internal CountedAirStrike(int armyId, int firstStrikeEta, bool bound)
        {
            ArmyId = armyId;
            FirstStrikeEta = firstStrikeEta;
            Bound = bound;
        }

        internal string Describe() => $"air#{ArmyId}{(Bound ? "(bound)" : "")}@eta{FirstStrikeEta}";
    }

    // What an Attack march will meet at its target, as the decisions about that march read it.
    internal readonly struct AttackSupportForecast
    {
        // The known package and hex bonus as they stand now.
        internal readonly IReadOnlyList<WorthIt.DefendingArmy> BaseOpposition;
        internal readonly float BaseHexBonus;
        // The same after the committed support has acted (== Base* when none is counted).
        internal readonly IReadOnlyList<WorthIt.DefendingArmy> Opposition;
        internal readonly float HexBonus;
        // The counted air support, in strike-series order (GroundCombatAirSupport.Series); empty
        // when no air support is counted.
        internal readonly IReadOnlyList<CountedAirStrike> Air;
        // The counted Disruption carrier (an own army on an active DisruptStructure leg).
        internal readonly int? DisruptCarrierArmyId;

        internal AttackSupportForecast(IReadOnlyList<WorthIt.DefendingArmy> baseOpposition, float baseHexBonus,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus,
            IReadOnlyList<CountedAirStrike> air, int? disruptCarrierArmyId)
        {
            BaseOpposition = baseOpposition;
            BaseHexBonus = baseHexBonus;
            Opposition = opposition;
            HexBonus = hexBonus;
            Air = air ?? System.Array.Empty<CountedAirStrike>();
            DisruptCarrierArmyId = disruptCarrierArmyId;
        }

        internal bool CountsSupport => (Air?.Count ?? 0) > 0 || DisruptCarrierArmyId.HasValue;

        internal string Describe() =>
            !CountsSupport ? "support=none"
                : "support=" + string.Join("+", (Air ?? System.Array.Empty<CountedAirStrike>())
                    .Select(s => s.Describe())
                    .Concat(DisruptCarrierArmyId.HasValue
                        ? new[] { $"disruption#{DisruptCarrierArmyId}" } : System.Array.Empty<string>()));
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
    //    · air: the operation's bound wings whose first strike lands no later than one turn after
    //      the fist could arrive (the terminal step waits that one turn:
    //      AttackMarchPolicy.WaitForAirStrike), then the best free formed wings / airfield
    //      launches that strike no later than a fist still at least a turn away (they are bound
    //      only once the operation starts), up to AiConfigV2.attackAirSupportMaxWings in all —
    //      GroundCombatAirSupport.Series, each wing priced on what the wings before it leave; its
    //      estimate and post-strike package, not a second model;
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
                return new AttackSupportForecast(known, hexBonus, known, hexBonus, null, null);

            IReadOnlyList<WorthIt.DefendingArmy> opposition = known;
            bool hasAircraft = snap.Self.Armies.Any(a => a != null && (a.IsAir || a.IsAirfield) && a.MemberCount > 0);
            bool carrierLeg = (snap.AllIntents ?? System.Array.Empty<MissionIntent>()).Any(i =>
                i?.Status == IntentStatus.Active && i.Scout?.Kind == ScoutTargetKind.DisruptStructure
                && i.Scout.FocusHex.Equals(targetHex));
            // A free carrier matters only where a structure bonus exists to be removed.
            bool freeCarrier = known.Any(a => a.BuildingDefenseBonus > 0f)
                && ScoutMoverSelector.HasStructuralCandidate(snap, DisruptionTarget(targetHex));
            if (!hasAircraft && !carrierLeg && !freeCarrier)
                return new AttackSupportForecast(known, hexBonus, known, hexBonus, null, null);
            int eta = armyEta?.Invoke() ?? int.MaxValue;
            List<CountedAirStrike> air = new List<CountedAirStrike>();
            if (hasAircraft)
                foreach ((AirSupportOption o, bool bound) in CountedAir(snap, targetHex, known, eta))
                {
                    opposition = o.OppositionAfter;
                    air.Add(new CountedAirStrike(o.WingArmyId, o.FirstStrikeEta, bound));
                }

            float projectedBonus = hexBonus;
            int? carrier = carrierLeg ? CountedCarrier(snap, targetHex, eta) : null;
            if (!carrier.HasValue && freeCarrier)
                carrier = FreeCarrier(snap, targetHex, eta);
            if (carrier.HasValue)
                opposition = WithoutStructure(opposition, hexBonus, out projectedBonus);
            return new AttackSupportForecast(known, hexBonus, opposition, projectedBonus, air, carrier);
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

        // The counted wings in strike-series order, each with whether the operation holds it.
        private static List<(AirSupportOption Option, bool Bound)> CountedAir(WorldSnapshot snap,
            HexCoord targetHex, IReadOnlyList<WorthIt.DefendingArmy> known, int armyEta)
        {
            var counted = new List<(AirSupportOption, bool)>();
            if (armyEta == int.MaxValue)
                return counted;
            MissionIntent operation = (snap.AllIntents ?? System.Array.Empty<MissionIntent>())
                .FirstOrDefault(i => i?.Status == IntentStatus.Active && i.Attack != null
                    && i.Attack.Target.Hex.Equals(targetHex));
            List<int> bound = operation?.Attack?.AirSupportWings.Select(w => w.ArmyId).ToList()
                ?? new List<int>();
            // A bound wing may strike up to one turn after the fist could arrive: the terminal step
            // waits for it (AttackMarchPolicy.WaitForAirStrike). A free wing is bound only once the
            // operation has started (AttackContinuity.ResolveAttackAirSupport, after the first
            // step), so it counts only for a march of at least a turn that it reaches no later.
            foreach (AirSupportOption o in GroundCombatAirSupport.Series(snap, known, targetHex,
                AirStrikePolicy.Standard, null, 0f, bound, WingsHeldElsewhere(snap, operation),
                AiConfigV2.attackAirSupportMaxWings,
                (c, isBound) => c.RosterKnown && c.OppositionAfter != null
                    && c.ExpectedDamage > AiConfigV2.allocatorSliceEpsilon
                    && (isBound ? c.FirstStrikeEta <= armyEta + 1
                        : armyEta >= 1 && c.FirstStrikeEta <= armyEta)))
                counted.Add((o, bound.Contains(o.WingArmyId)));
            return counted;
        }

        // Wings another operation already bound: not this march's support.
        private static HashSet<int> WingsHeldElsewhere(WorldSnapshot snap, MissionIntent self)
        {
            var held = new HashSet<int>();
            foreach (MissionIntent i in snap.AllIntents ?? System.Array.Empty<MissionIntent>())
            {
                if (i == null || i.Status != IntentStatus.Active || ReferenceEquals(i, self))
                    continue;
                foreach (AttackAirWing w in i.Attack?.AirSupportWings ?? Enumerable.Empty<AttackAirWing>())
                    held.Add(w.ArmyId);
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

        // ---- Strike sources for a site (2026-11-13, match Sable vs Korrin) ----
        // THE set of strike sources that can serve `targetHex` now: a strike aircraft in hand, a
        // Research/Production source minting one (a free hand slot and generation left), an Air
        // Bomb in hand or minted whose own carrier then serves the site. Each counts only when
        // the deployment's own placement and service projection prove a legal strike
        // (NonCombatCardPlayer.CanStructurallyStrike, GroundCombatAirSupport.BombedService) on a
        // still uncovered request of the site (KnownStrikeTargets: the live operation's request
        // and the missile-stock request of an enemy army standing there). Read by Phase A's strike
        // need (PhaseAOperationSupport.Assess) and by the preparation's WAIT witness
        // (AttackContinuity) - one answer to "can a strike still be produced for this site".
        // A source counts only while stock plus devChainFundingHorizonTurns of income pays it (the
        // timing rule of a minted roster card): a strike the income never reaches holds nothing.
        internal readonly struct StrikeSource
        {
            internal readonly ResourceCost Cost;
            internal readonly string Label;
            internal StrikeSource(ResourceCost cost, string label) { Cost = cost; Label = label; }
        }

        internal static List<StrikeSource> StrikeSources(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, IReadOnlyList<CardData> hand, bool handHasFreeSlot,
            bool canGenerate, HexCoord targetHex, out List<CombatAirSupportRequest> requests)
        {
            requests = new List<CombatAirSupportRequest>();
            var result = new List<StrikeSource>();
            if (snap == null || player == null || root == null || ctx?.Map == null)
                return result;
            hand = hand ?? System.Array.Empty<CardData>();
            IReadOnlyList<GenerationStep> forecast = canGenerate
                ? snap.Development?.ForecastSources ?? System.Array.Empty<GenerationStep>()
                : System.Array.Empty<GenerationStep>();
            List<CardData> handStrikes = hand.Where(c => IsStrikeCard(c?.Definition)).ToList();
            List<GenerationStep> mintedStrikes = handHasFreeSlot
                ? forecast.Where(g => IsStrikeCard(g?.CardDef)).ToList() : new List<GenerationStep>();
            List<CardData> handBombs = hand.Where(c => GroundCombatAirSupport.IsAircraftBomb(c?.Definition)).ToList();
            // A bomb needs no hand slot until it is made (2026-11-09, owner, match P6a).
            List<GenerationStep> mintedBombs = forecast
                .Where(g => GroundCombatAirSupport.IsAircraftBomb(g?.CardDef)).ToList();
            if (handStrikes.Count == 0 && mintedStrikes.Count == 0 && handBombs.Count == 0 && mintedBombs.Count == 0)
                return result;
            requests = NonCombatCardPlayer.KnownStrikeTargets(snap, player, root, ctx,
                    ReconObjectiveEvaluator.FrameOrEnumerate(snap).Where(o => o != null).ToList())
                .Where(r => r.Target.Equals(targetHex)).ToList();
            if (requests.Count == 0)
                return result;
            foreach (CardData c in handStrikes)
                if (NonCombatCardPlayer.CanStructurallyStrike(snap, player, root, ctx, c, requests))
                    result.Add(new StrikeSource(c.EffectivePlayResourceCost, "hand:" + c.Definition.displayName));
            foreach (GenerationStep g in mintedStrikes)
            {
                var minted = new CardData(g.CardDef) { ResearchProductionCreated = true };
                if (NonCombatCardPlayer.CanStructurallyStrike(snap, player, root, ctx, minted, requests))
                    result.Add(new StrikeSource(StrategicCardEvaluator.AddResourceCosts(
                        minted.EffectivePlayResourceCost, g.ChallengeCard?.resourceCost),
                        "generate:" + g.CardDef.displayName));
            }
            foreach (CardData c in handBombs)
                if (BombCarrierStrikes(snap, player, ctx, c.Definition, requests))
                    result.Add(new StrikeSource(c.EffectivePlayResourceCost, "hand-bomb:" + c.Definition.displayName));
            foreach (GenerationStep g in mintedBombs)
                if (BombCarrierStrikes(snap, player, ctx, g.CardDef, requests))
                    result.Add(new StrikeSource(StrategicCardEvaluator.AddResourceCosts(g.CardDef.resourceCost,
                        g.ChallengeCard?.resourceCost), "generate-bomb:" + g.CardDef.displayName));
            result.RemoveAll(x => !AggressionDemandEvaluator.FundableWithinHorizon(snap, x.Cost,
                AiConfigV2.devChainFundingHorizonTurns));
            return result;
        }

        // An aircraft that strikes ground (a missile or a bomber), never a recon-only frame.
        internal static bool IsStrikeCard(CardDefinition def) =>
            def?.isAviation == true
            && ExplosionAttack.AttackPower(def.attack, def.grantedAbilities) > 0;

        // Some own aircraft, carrying `bomb`, serves a strike on the site from where it stands.
        private static bool BombCarrierStrikes(WorldSnapshot snap, PlayerSetupData player,
            AiTurnContext ctx, CardDefinition bomb, IReadOnlyList<CombatAirSupportRequest> requests) =>
            OwnWorld.Of(player).Armies.Where(a => a != null)
                .SelectMany(a => a.Members)
                .Any(u => u != null && u.IsAviation
                    && GroundCombatAirSupport.BombedService(snap, player, ctx, u, bomb, requests).HasValue);
    }
}
