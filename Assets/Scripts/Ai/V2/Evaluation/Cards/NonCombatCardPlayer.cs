using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  NON-COMBAT SURPLUS CARD PLAY  (Strategy V2 — Strategic Manager Phase B, spec §5/§13)
    // ===========================================================================================
    //  RankedSurplus owns Unit / Hero / solo-Recce materialization (and chained Equipment).
    //  This peer lane owns Aviation, surplus Facilities and standalone Equipment. Base founding
    //  belongs to Economy; Research/Production facilities belong to Development (its investment
    //  window + preparation EV, built in Phase A). Both surplus lanes enumerate their complete
    //  admissible alternatives before the common Phase-B arbiter ranks them.
    //
    //  It enumerates every hand/generated card through a pure type router and checks it against
    //  the SAME canonical gameplay APIs the human UI / V1 AI use (BuildingPlayExecutor ->
    //  InfrastructureActions, AviationActions.TryDeployFromCard, EquipmentSystem), then hands
    //  StrategicPhaseB the complete preflighted candidate set.
    //  Rejections report gameplay feasibility (AP, resources, placement, capacity, host) or an
    //  explicit operation owner: Base cards require Economy; R/P facilities require Development.
    // ===========================================================================================
    internal static class NonCombatCardPlayer
    {
        internal enum PlayKind { Base, Facility, Aviation, Equipment }

        internal sealed class NonCombatPlay
        {
            public CardData Card;
            public PlayKind Kind;
            public HexCoord TargetHex;
            public UnitData EquipHost;   // Equipment only
            public float Score;
            public float ApCost;
            public ResourceCost ResCost;
            public string StableKey;
            public string Explain;
            // Set when this play must first MINT its card through a
            // Research/Production Challenge (Card is then a throwaway pre-mint stand-in; Execute
            // re-resolves the placement against the real minted instance). null => Card is a real
            // hand card.
            public GenerationStep Generation;
        }

        // Every non-combat card is scored through the shared StrategicCardEvaluator (same
        // breakdown / NetScore band as a Unit/Hero chain), so Phase B can compare the two lanes
        // directly.
        // PlayKind.Base has no arm — CardType.Base is blocked earlier in enumeration
        // (requires_economy_expansion_demand, see below) and never reaches this call.
        private static NonCombatRole RoleOf(PlayKind k) => k switch
        {
            PlayKind.Facility => NonCombatRole.Facility,
            PlayKind.Aviation => NonCombatRole.Aviation,
            _ => NonCombatRole.Equipment,
        };

        private static StrategicCardUseCandidate Evaluate(WorldSnapshot snap, PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, PlayKind k, CardData card, AiHandData hand, float bestEquipmentUpgrade,
            float apCost, ResourceCost resCost, GenerationStep generation = null,
            float? witnessedUsefulApDemand = null, bool logDynamicEffect = true,
            TaskScore? operationalTask = null)
        {
            CapabilityInventory inv = CapabilityInventory.Build(snap, player, null);
            StrategicCardUseCandidate cand = StrategicCardEvaluator.ScoreNonCombat(
                RoleOf(k), card, snap, inv, hand, bestEquipmentUpgrade, generation,
                witnessedUsefulApDemand, apCost, resCost,
                type => StrategicSpendability.SpendableAmount(player, root, ctx, type), player,
                operationalTask);
            // AI-MGR §15 — surface the dynamic-effect decomposition (PlayerGlobal ApBonus value on a
            // Base / Facility, priced by the SAME model as a Hero) so the non-combat lane is testable.
            if (logDynamicEffect && !string.IsNullOrEmpty(cand.Breakdown?.EffectDetail))
                AiDebugLog.Write($"[AI][V2]   strat.nonCombat — {card?.Definition?.displayName} "
                    + $"role={cand.IntendedRole} net {cand.NetScore.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"[{cand.Breakdown.ToCompact()}]");
            return cand;
        }

        private static float Score(WorldSnapshot snap, PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, PlayKind k, CardData card, AiHandData hand, float bestEquipmentUpgrade,
            float apCost, ResourceCost resCost, GenerationStep generation = null,
            float? witnessedUsefulApDemand = null, bool logDynamicEffect = true,
            TaskScore? operationalTask = null) =>
            Evaluate(snap, player, root, ctx, k, card, hand, bestEquipmentUpgrade,
                apCost, resCost, generation, witnessedUsefulApDemand, logDynamicEffect,
                operationalTask).NetScore;

        // Capacity-upgrade look-ahead must value the exact Facility it would unlock through the
        // same scorer as an ordinary legal Facility play. Keeping this thin adapter here prevents
        // StrategicMaintenancePolicy from assembling a second non-combat evaluation context.
        internal static StrategicCardUseCandidate ScoreCapacityUnlock(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, CardData card, AiHandData hand,
            float? witnessedUsefulApDemand = null) =>
            Evaluate(snap, player, root, ctx, PlayKind.Facility, card, hand, 0f,
                card != null ? card.EffectivePlayApCost : 0f,
                card?.EffectivePlayResourceCost, generation: null,
                witnessedUsefulApDemand: witnessedUsefulApDemand,
                logDynamicEffect: false);

        // Preparation-only preview for aviation: use the SAME generated non-combat play
        // builder as Phase B: real airfield capacity, exact resources, Challenge + deploy AP
        // and canonical aviation scoring. Only current-turn AP admission is omitted for a
        // FUTURE investment; actual Phase B deployment keeps the full live AP check.
        // No fake ground placement or second aviation valuation. Nothing is minted here.
        internal static float ProjectedAviationInvestmentValue(CardDefinition card,
            ResearchProductionMode mode, HexCoord facilityHex, UnitData operatorHero,
            WorldSnapshot snap, PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx)
        {
            if (card == null || !card.isAviation || operatorHero == null || snap == null
                || player == null || root == null || hand?.Hand == null || ctx == null
                || string.IsNullOrWhiteSpace(card.authoredKey))
                return float.NegativeInfinity;
            var generation = new GenerationStep
            {
                Mode = mode, FacilityHex = facilityHex, Hero = operatorHero, CardDef = card,
                SuccessChance = ResearchProductionSystem.EstimateSuccessChance(operatorHero, card),
                ProducesEquipment = false, UseKey = "investment-preview",
                CardKey = "investment-preview:" + card.authoredKey,
            };
            var standIn = new CardData(card) { ResearchProductionCreated = true };
            NonCombatPlay preview = BuildPlayFor(standIn, generation, snap, player, root,
                hand, ctx, OwnedBaseHexes(snap, player), new List<string>(),
                investmentPreview: true);
            return preview != null && preview.Kind == PlayKind.Aviation
                ? preview.Score : float.NegativeInfinity;
        }

        // Pure card-type router: which Phase-B lane owns this card. null => the Unit/Hero/Recce
        // materialization chain (MaterializationCandidateBuilder) owns it. Exhaustive over
        // CardType — no card falls through to "no lane", so card type is never on its own a reason
        // a legal card is left unplayed.
        internal static PlayKind? LaneFor(CardDefinition def)
        {
            if (def == null)
                return null;
            if (def.isAviation)
                return PlayKind.Aviation;
            bool isRecce = AbilityParams.AbilitiesHaveAnyRecce(def.grantedAbilities);
            if (def.cardType == CardType.Unit || def.cardType == CardType.Hero || isRecce)
                return null; // combat body -> materialization chain
            switch (def.cardType)
            {
                case CardType.Base: return PlayKind.Base;
                case CardType.Facility: return PlayKind.Facility;
                case CardType.Equipment: return PlayKind.Equipment;
                default: return null;
            }
        }

        public static NonCombatPlay BestPlay(WorldSnapshot snap, PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, out List<string> blocked, PlayKind? onlyKind = null,
            MaterializationReservation reservation = null, float? witnessedUsefulApDemand = null)
        {
            blocked = new List<string>();
            NonCombatPlay best = null;
            foreach (NonCombatPlay p in EnumeratePlays(snap, player, root, hand, ctx, blocked, reservation,
                         witnessedUsefulApDemand))
                if ((onlyKind == null || p.Kind == onlyKind.Value)
                    && (best == null || p.Score > best.Score
                        || (System.Math.Abs(p.Score - best.Score) <= 0.0001f
                            && string.CompareOrdinal(p.StableKey, best.StableKey) < 0)))
                    best = p;
            if (best?.Kind == PlayKind.Aviation)
                AiDebugLog.Write($"[AI][V2][Aviation][Deployment] card={best.Card.Definition.displayName} "
                    + $"airfield=({best.TargetHex.Q},{best.TargetHex.R}) decision=SELECT "
                    + $"score={best.Score:0.00} detail=\"{best.Explain}\"");
            return best;
        }

        // Every LEGAL non-combat play for the current hand (each already
        // resolved to a real placement / host / airfield slot / base slot by BuildPlayFor).
        // BestPlay is a convenience caller; Phase-B arbitration and reaction probes consume the whole set
        // so it can find the genuinely CHEAPEST feasible reaction, not the best-scored card.
        internal static IEnumerable<NonCombatPlay> EnumeratePlays(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, List<string> blocked,
            MaterializationReservation reservation = null, float? witnessedUsefulApDemand = null)
        {
            if (player == null || root == null || hand?.Hand == null || ctx == null)
                yield break;
            if (blocked == null) blocked = new List<string>();

            var ownBaseHexes = OwnedBaseHexes(snap, player);

            foreach (CardData card in hand.Hand.ToList())
            {
                CardDefinition def = card?.Definition;
                if (def == null)
                    continue;
                // Respect physical Economy reservations before placement enumeration. Base
                // founding itself is excluded below for every copy, including generated cards.
                if (reservation != null && reservation.ClaimsEconomyBuildCard(card))
                {
                    blocked.Add($"{def.displayName}:reservedForEconomyBuild");
                    continue;
                }
                // A non-aviation Unit / Hero / solo-Recce card is the materialization chain's job.
                if (!def.isAviation
                    && (def.cardType == CardType.Unit || def.cardType == CardType.Hero
                        || AbilityParams.AbilitiesHaveAnyRecce(def.grantedAbilities)))
                    continue;
                if (def.isAviation)
                {
                    foreach (NonCombatPlay aviation in BuildAviationPlays(card, null, snap,
                        player, root, hand, ctx, blocked, witnessedUsefulApDemand))
                        yield return aviation;
                    continue;
                }
                NonCombatPlay p = BuildPlayFor(card, generation: null, snap, player, root, hand, ctx,
                    ownBaseHexes, blocked, witnessedUsefulApDemand);
                if (p != null)
                    yield return p;
            }

            // Generated non-combat cards. A Research/Production
            // Challenge whose minted card is an Aviation / Base / Facility is scored on the SAME
            // NetScore band (throwaway pre-mint stand-in), discounted by the Challenge success
            // chance + the generation step penalty; Execute mints then deploys via the canonical
            // API. Generated Equipment stays with the materialization GenerateAttachDeploy chain.
            if (reservation != null && reservation.CanGenerateMore)
            {
                foreach (GenerationStep g in GenerationSource.Enumerate(player, root, ctx, hand,
                    reservation.ClaimedGeneratorUses, reservation.TriedGeneratorCards))
                {
                    CardDefinition gd = g?.CardDef;
                    if (gd == null || g.ProducesEquipment)
                        continue;
                    if (!(gd.isAviation || gd.cardType == CardType.Base || gd.cardType == CardType.Facility))
                        continue;
                    var stand = new CardData(gd) { ResearchProductionCreated = true };
                    if (gd.isAviation)
                    {
                        foreach (NonCombatPlay aviation in BuildAviationPlays(stand, g, snap,
                            player, root, hand, ctx, blocked, witnessedUsefulApDemand))
                        {
                            aviation.Explain = $"generate:{gd.displayName} -> " + aviation.Explain;
                            yield return aviation;
                        }
                        continue;
                    }
                    NonCombatPlay p = BuildPlayFor(stand, g, snap, player, root, hand, ctx,
                        ownBaseHexes, blocked, witnessedUsefulApDemand);
                    if (p == null)
                        continue;
                    p.Explain = $"generate:{gd.displayName} -> " + p.Explain;
                    yield return p;
                }
            }
        }

        // One non-combat play for one card (real hand card, or a pre-mint stand-in when
        // `generation` is set), so the generated path reuses the exact same placement resolution +
        // scoring.
        private static NonCombatPlay BuildPlayFor(CardData card, GenerationStep generation,
            WorldSnapshot snap, PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, List<HexCoord> ownBaseHexes, List<string> blocked,
            float? witnessedUsefulApDemand = null, bool investmentPreview = false)
        {
            CardDefinition def = card?.Definition;
            if (def == null)
                return null;

            float playAp = def.isAviation ? CardCostRules.PlayAp(card) : card.EffectivePlayApCost;
            float totalAp = playAp + (generation != null
                ? ResearchProductionSystem.AttemptApCost(generation.CardDef) : 0f);
            ResourceCost totalRes = CombinedCost(card.EffectivePlayResourceCost,
                generation?.CardDef?.resourceCost);
            int handOrdinal = hand.Hand.IndexOf(card);
            string sourceKey = generation != null
                ? "gen:" + generation.CardKey
                : $"hand:{handOrdinal}:{def.authoredKey ?? "?"}";

            if (def.isAviation)
            {
                return BuildAviationPlays(card, generation, snap, player, root, hand, ctx,
                        blocked, witnessedUsefulApDemand, investmentPreview)
                    .OrderByDescending(p => p.Score)
                    .ThenBy(p => p.StableKey, System.StringComparer.Ordinal)
                    .FirstOrDefault();
            }

            if (def.cardType == CardType.Facility)
            {
                if (def.grantedAbilities?.Contains(ResearchProductionSystem.FacilityAbility(ResearchProductionMode.Research)) == true
                    || def.grantedAbilities?.Contains(ResearchProductionSystem.FacilityAbility(ResearchProductionMode.Production)) == true)
                {
                    blocked.Add($"{def.displayName}:facility(owned_by_development_preparation)");
                    return null;
                }
                HexCoord? at = null;
                int bestReadiness = -1;
                string why = "noOwnedBase";
                foreach (HexCoord h in ownBaseHexes)
                {
                    if (!BuildingPlayExecutor.CanPlaceFacilityAt(player, hand, ctx, card, h, out string r,
                            requireCardInHand: generation == null))
                    {
                        if (r != null) why = r;
                        continue;
                    }

                    // Prefer the base where this Facility's matching Research/Production actor
                    // already stands: placement then unlocks utility immediately. Coordinates are
                    // only the deterministic final tie-break through ownBaseHexes ordering.
                    int readiness = FacilityImmediateReadiness(def, player, h);
                    if (at == null || readiness > bestReadiness)
                    {
                        at = h;
                        bestReadiness = readiness;
                    }
                }
                if (at == null)
                {
                    blocked.Add($"{def.displayName}:facility({why})");
                    return null;
                }
                return new NonCombatPlay
                {
                    Card = card, Kind = PlayKind.Facility, TargetHex = at.Value, Generation = generation,
                    ApCost = totalAp, ResCost = totalRes,
                    Score = Score(snap, player, root, ctx, PlayKind.Facility, card, hand, 0f,
                        totalAp, totalRes, generation, witnessedUsefulApDemand),
                    StableKey = $"{sourceKey}:facility:{at.Value.Q},{at.Value.R}",
                    Explain = $"{def.displayName} -> Base ({at.Value.Q},{at.Value.R})",
                };
            }

            if (def.cardType == CardType.Base)
            {
                // Founding is a target-specific Economy operation: Demand evaluates the site,
                // Phase A stages its builder/card and TaskExecutor completes the admitted build.
                // A free hero and surplus AP cannot authorize a second, independent expansion.
                // Keep this exclusion in the shared enumeration so tempo, HandFollowup witnesses
                // and Phase-B AP workload all see the same executable universe, including cards
                // offered by generation. Newly useful sites re-enter the existing Economy loop.
                blocked.Add($"{def.displayName}:base(requires_economy_expansion_demand)");
                return null;
            }

            if (def.cardType == CardType.Equipment && def.equipment != null)
            {
                (UnitData unit, HexCoord hex, float upgrade, string stableKey)? host =
                    BestEquipmentHost(player, root, card, snap);
                if (host == null)
                {
                    blocked.Add($"{def.displayName}:equipment(noLegalDeployedHost)");
                    return null;
                }
                // Host is chosen by StrategicCardEvaluator.EquipmentUpgradeValue, not by raw host
                // power, and that same value is the RoleFit.
                return new NonCombatPlay
                {
                    Card = card, Kind = PlayKind.Equipment, EquipHost = host.Value.unit,
                    TargetHex = host.Value.hex, Generation = generation,
                    ApCost = totalAp, ResCost = totalRes,
                    Score = Score(snap, player, root, ctx, PlayKind.Equipment, card, hand, host.Value.upgrade,
                        totalAp, totalRes, generation, witnessedUsefulApDemand),
                    StableKey = $"{sourceKey}:equipment:{host.Value.stableKey}",
                    Explain = $"{def.displayName} -> {host.Value.unit.Name} (Δ{host.Value.upgrade:0.00})",
                };
            }

            blocked.Add($"{def.displayName}:{def.cardType}(noNonCombatPlayPath)");
            return null;
        }

        private static List<NonCombatPlay> BuildAviationPlays(CardData card,
            GenerationStep generation, WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, List<string> blocked,
            float? witnessedUsefulApDemand = null, bool investmentPreview = false)
        {
            var result = new List<NonCombatPlay>();
            CardDefinition def = card?.Definition;
            if (def == null)
                return result;
            List<HexCoord> airfields = PlacementRules.EnumerateAviationPlacements(
                snap, player, root, card, out string why,
                requireCurrentAp: !investmentPreview);
            if (airfields.Count == 0)
            {
                blocked?.Add($"{def.displayName}:aviation({why ?? "noAirfieldSlot"})");
                return result;
            }

            float playAp = CardCostRules.PlayAp(card);
            float totalAp = playAp + (generation != null
                ? ResearchProductionSystem.AttemptApCost(generation.CardDef) : 0f);
            ResourceCost totalRes = CombinedCost(card.EffectivePlayResourceCost,
                generation?.CardDef?.resourceCost);
            int handOrdinal = hand.Hand.IndexOf(card);
            string sourceKey = generation != null
                ? "gen:" + generation.CardKey
                : $"hand:{handOrdinal}:{def.authoredKey ?? "?"}";
            List<ReconObjective> objectives = ReconObjectiveEvaluator.Enumerate(snap)
                .Where(o => o != null).ToList();

            foreach (HexCoord airfield in airfields)
            {
                TaskScore service = BestAirfieldServiceTaskScore(
                    snap, player, ctx, def, airfield, objectives,
                    out int coverage, out string witness);
                float score = Score(snap, player, root, ctx, PlayKind.Aviation, card, hand, 0f,
                    totalAp, totalRes, generation, witnessedUsefulApDemand,
                    logDynamicEffect: true, operationalTask: service);
                int free = AiAirSortiePlanner.FreeLandingCapacity(airfield, player);
                AiDebugLog.Write($"[AI][V2][Aviation][Deployment] card={def.displayName} "
                    + $"airfield=({airfield.Q},{airfield.R}) capacity={free} "
                    + $"objectiveCoverage={coverage} task={service.Value:0.00} "
                    + $"decision=CANDIDATE witness={witness ?? "none"}");
                result.Add(new NonCombatPlay
                {
                    Card = card,
                    Kind = PlayKind.Aviation,
                    TargetHex = airfield,
                    Generation = generation,
                    ApCost = totalAp,
                    ResCost = totalRes,
                    Score = score,
                    StableKey = $"{sourceKey}:aviation:{airfield.Q},{airfield.R}",
                    Explain = $"{def.displayName} -> airfield ({airfield.Q},{airfield.R}); "
                        + $"runnableAirObjectives={coverage}, serviceTask={service.Value:0.00}, "
                        + $"witness={witness ?? "none"}",
                });
            }
            return result;
        }

        // Placement refinement remains on the canonical TaskScore scale. Each candidate must
        // prove an actual safe sortie (capacity + range + known-AA filtering are owned by
        // AiAirSortiePlanner); the best currently runnable Recon objective supplies every positive
        // value component. Airfield threat is folded through TaskScore.HexThreatRisk, not a
        // forward-base multiplier.
        internal static TaskScore BestAirfieldServiceTaskScore(WorldSnapshot snap,
            PlayerSetupData player, AiTurnContext ctx, CardDefinition def, HexCoord airfield,
            IReadOnlyList<ReconObjective> objectives, out int coverage, out string witness)
        {
            coverage = 0;
            witness = null;
            if (ctx?.Map == null || def == null || !def.isAviation)
                return default;
            var projected = new List<UnitData>
            {
                new UnitData
                {
                    Owner = player, IsAviation = true,
                    MoveMax = Mathf.Max(1, def.moveMax),
                    MoveCurrent = Mathf.Max(1, def.moveMax),
                    ActivationApCost = Mathf.Max(0, def.activationApCost),
                    LaunchEnergyCost = Mathf.Max(0, def.launchEnergyCost),
                    TurnsWithoutRefuel = Mathf.Max(0, def.turnsWithoutRefuel),
                },
            };
            return BestAirfieldServiceTaskScore(snap, player, ctx, projected, airfield,
                objectives, out coverage, out witness);
        }

        // Live-aircraft overload used by the generic Rebase operation. Deployment and later
        // reassignment deliberately share this one service projection; there is no second local
        // "forward base" score after the aircraft exists.
        internal static TaskScore BestAirfieldServiceTaskScore(WorldSnapshot snap,
            PlayerSetupData player, AiTurnContext ctx, IReadOnlyList<UnitData> projected,
            HexCoord airfield, IReadOnlyList<ReconObjective> objectives,
            out int coverage, out string witness)
        {
            coverage = 0;
            witness = null;
            if (ctx?.Map == null || projected == null || projected.Count == 0
                || projected.Any(u => u == null || !u.IsAviation))
                return default;
            float airfieldThreat = snap?.Threat?.Threats?
                .Where(t => t?.Asset != null && t.Asset.Hex.Equals(airfield)
                    && (t.Asset.Kind == AssetKind.Citadel || t.Asset.Kind == AssetKind.Base))
                .Select(t => t.Severity).DefaultIfEmpty(0f).Max() ?? 0f;

            TaskScore best = default;
            float bestValue = float.NegativeInfinity;
            foreach (ReconObjective objective in objectives ?? System.Array.Empty<ReconObjective>())
            {
                // Only jobs the Recon owner will actually hand to an aircraft carry value here:
                // the aviation-only AirSweep pass (ReconAirCapacityPolicy.IsAirServiceable).
                if (!ReconAirCapacityPolicy.IsAirServiceable(objective))
                    continue;
                // An AirSweep is served by flying TOWARD its anchor as deep as these aircraft's
                // refuel endurance allows (plane: half its move; helicopter: its whole move) and
                // returning — prove that sortie, not a flight all the way to the anchor.
                HexCoord sweepPoint = ReconAirCapacityPolicy.SweepEndpoint(airfield, objective.FocusHex,
                    ReconAirCapacityPolicy.SweepReach(projected));
                if (sweepPoint.Equals(airfield))
                    continue;
                Sortie? sameTurn = AiAirSortiePlanner.TryPlanSortieFromStorage(
                    airfield, projected, sweepPoint, ctx.Map, player);
                MultiTurnSortie? multiTurn = sameTurn.HasValue ? null
                    : AiAirSortiePlanner.TryPlanMultiTurnSortieFromStorage(
                        airfield, projected, sweepPoint, ctx.Map, player);
                if (!sameTurn.HasValue && !multiTurn.HasValue)
                    continue;
                coverage++;
                int eta = sameTurn.HasValue ? 1 : Mathf.Max(1, multiTurn.Value.RequiredTurns);
                int routeCost = sameTurn.HasValue
                    ? sameTurn.Value.TotalCost : multiTurn.Value.TotalRouteCost;
                int moveMax = projected.Select(AviationRules.EffectiveMoveMax)
                    .DefaultIfEmpty(1).Min();
                int activationAp = projected.Sum(u => Mathf.Max(0, u.ActivationApCost));
                TaskScore candidate = CopyReconScoreWithAirDelivery(objective.TaskScore,
                    eta, routeCost, moveMax, activationAp,
                    TaskScoreEvaluator.HexThreatRisk(airfieldThreat));
                if (candidate.Value > bestValue)
                {
                    bestValue = candidate.Value;
                    best = candidate;
                    witness = $"{objective.Kind}@({objective.FocusHex.Q},{objective.FocusHex.R})"
                        + $"/eta={eta}/route={routeCost}";
                }
            }
            return coverage > 0 ? best : default;
        }

        private static TaskScore CopyReconScoreWithAirDelivery(TaskScore s, int eta,
            int routeCost, int moveMax, int activationAp, float airfieldThreatRisk)
        {
            float movementShare = Mathf.Clamp01(Mathf.Max(0, routeCost)
                / (float)Mathf.Max(1, moveMax * Mathf.Max(1, eta)));
            float routeOpportunity = movementShare * Mathf.Max(0, activationAp)
                * AiConfigV2.taskScoreReactivationApWeight;
            return new TaskScore(
            economicHexBenefit: s.EconomicHexBenefit, payback: s.Payback,
            airfield: s.Airfield, globalCardEffect: s.GlobalCardEffect,
            infoGain: s.InfoGain, staleness: s.Staleness,
            strategicRelevance: s.StrategicRelevance, threatDirection: s.ThreatDirection,
            contactRelevance: s.ContactRelevance, frontProgress: s.FrontProgress,
            corridorAlignment: s.CorridorAlignment,
            ownTerritoryProximity: s.OwnTerritoryProximity, terrainDefense: s.TerrainDefense,
            militaryTargetRelevance: s.MilitaryTargetRelevance, winChance: s.WinChance,
            cardPrice: s.CardPrice,
            delivery: TaskScoreEvaluator.DeliveryFromEta(
                Mathf.Max(0, activationAp), eta, AiConfigV2.taskScoreReactivationApWeight),
            moverOpportunityCost: routeOpportunity,
            hexThreatRisk: s.HexThreatRisk + airfieldThreatRisk,
            detectionRisk: s.DetectionRisk,
            economicExpansionValue: s.EconomicExpansionValue);
        }

        // A structured result. A generated non-combat play is NOT atomic
        // (mint then deploy), so a partial failure — Challenge lost after resources were spent /
        // the Researcher was revealed, OR a mint that then can't be deployed — really changes state
        // and consumes the turn's generation attempt. The caller must see that, not just `false`.
        internal struct NonCombatExecuteResult
        {
            public bool Played;               // final deploy/attach/build succeeded
            public bool StateChanged;         // ANY real world mutation happened (mint, reveal, resource spend, deploy)
            public bool GenerationAttempted;  // a Challenge was rolled (attempt is spent either way)
            public bool Generated;            // the Challenge won and a card is now in hand
            public float ApSpent;
            public string FailReason;
        }

        public static NonCombatExecuteResult Execute(NonCombatPlay play, WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            var res = new NonCombatExecuteResult();
            if (play?.Card?.Definition == null || player == null || root == null || hand?.Hand == null || ctx == null)
            {
                res.FailReason = "missing args";
                return res;
            }

            int apBefore = root.ActionPoints;
            int stateVersionBefore = V2StateVersion.Current;

            // finding 9b — a generated non-combat play pays Challenge AP + ResourceCost,
            // then probabilistically mints and deploys the REAL instance. finding P1 — every real
            // mutation of this non-atomic chain is reported even when a later step fails.
            if (play.Generation != null)
            {
                MaterializationExecutor.GenerationOutcome go =
                    MaterializationExecutor.TryGenerate(play.Generation, player, root, hand, ctx);
                res.GenerationAttempted = go.Attempted;
                if (go.StateChanged) res.StateChanged = true;
                if (!go.Success)
                {
                    res.ApSpent = System.Math.Max(0, apBefore - root.ActionPoints);
                    res.FailReason = go.FailReason ?? "generation failed";
                    StampVersion(ref res, stateVersionBefore);
                    return res;
                }
                res.Generated = true;
                res.StateChanged = true;   // a card was minted into the hand

                // Re-resolve the placement against the real card + live world (the pre-mint
                // stand-in's target may be stale). The minted card stays in hand if this fails —
                // a real asset, exactly like CardPlayExecutor keeping a created empty army.
                NonCombatPlay fresh = BuildPlayFor(go.Minted, generation: null, snap, player, root,
                    hand, ctx, OwnedBaseHexes(snap, player), new List<string>());
                if (fresh == null || fresh.Kind != play.Kind)
                {
                    res.ApSpent = System.Math.Max(0, apBefore - root.ActionPoints);
                    res.FailReason = "no legal placement for the generated non-combat card";
                    StampVersion(ref res, stateVersionBefore);
                    return res;
                }
                play = fresh;
            }

            if (!hand.Hand.Contains(play.Card))
            {
                res.ApSpent = System.Math.Max(0, apBefore - root.ActionPoints);
                res.FailReason = "card no longer in hand";
                StampVersion(ref res, stateVersionBefore);
                return res;
            }

            bool ok;
            string failReason = null;
            switch (play.Kind)
            {
                case PlayKind.Aviation:
                {
                    ok = AviationActions.TryDeployFromCard(play.Card.Definition, player, root,
                        ctx.HexSelection, play.TargetHex, out failReason, null, play.Card);
                    if (ok)
                        hand.RemoveCard(play.Card);
                    break;
                }
                case PlayKind.Base:
                {
                    BuildingPlayResult r = BuildingPlayExecutor.PlayBaseCard(player, root, hand, ctx, play.Card, play.TargetHex);
                    ok = r.Built;
                    failReason = r.FailReason;
                    break;
                }
                case PlayKind.Facility:
                {
                    BuildingPlayResult r = BuildingPlayExecutor.PlayFacilityCard(player, root, hand, ctx, play.Card, play.TargetHex);
                    ok = r.Built;
                    failReason = r.FailReason;
                    break;
                }
                case PlayKind.Equipment:
                {
                    if (play.EquipHost == null)
                    {
                        failReason = "equipment host gone";
                        ok = false;
                        break;
                    }
                    ok = EquipmentSystem.TryAttach(play.Card, play.EquipHost, root, out failReason);
                    if (ok)
                        hand.RemoveCard(play.Card);
                    break;
                }
                default:
                    failReason = "unknown non-combat play kind";
                    ok = false;
                    break;
            }

            res.Played = ok;
            if (ok) res.StateChanged = true;
            res.ApSpent = System.Math.Max(0, apBefore - root.ActionPoints);
            if (!ok) res.FailReason = failReason ?? "non-combat play failed";
            StampVersion(ref res, stateVersionBefore);
            return res;
        }

        // ------------------------------------------------------------------ helpers ----

        private static int FacilityImmediateReadiness(
            CardDefinition def, PlayerSetupData player, HexCoord hex)
        {
            if (def?.grantedAbilities == null || player == null)
                return 0;

            int actors = 0;
            if (def.grantedAbilities.Contains(
                    ResearchProductionSystem.FacilityAbility(ResearchProductionMode.Research)))
                actors += ResearchProductionSystem.FindActors(
                    player, hex, ResearchProductionMode.Research).Count;
            if (def.grantedAbilities.Contains(
                    ResearchProductionSystem.FacilityAbility(ResearchProductionMode.Production)))
                actors += ResearchProductionSystem.FindActors(
                    player, hex, ResearchProductionMode.Production).Count;
            return actors;
        }

        private static List<HexCoord> OwnedBaseHexes(WorldSnapshot snap, PlayerSetupData player)
        {
            var set = new HashSet<HexCoord>();
            foreach (BuildingData b in BuildingRegistry.AllBuildings())
                if (b != null && b.Owner == player && b.IsBase)
                    set.Add(b.Hex);
            if (snap?.Self?.BaseHexes != null)
                foreach (HexCoord h in snap.Self.BaseHexes)
                    set.Add(h);
            if (snap?.Self != null)
                set.Add(snap.Self.Citadel);
            return set.OrderBy(h => h.Q).ThenBy(h => h.R).ToList();
        }

        // The legal host that maximises StrategicCardEvaluator.EquipmentUpgradeValue (the ONE
        // equipment value: predicted delta x known-threat matchup x persistence), name only as
        // the final deterministic tie-break.
        private static (UnitData unit, HexCoord hex, float upgrade, string stableKey)? BestEquipmentHost(
            PlayerSetupData player, PlayerRoot root, CardData equipCard, WorldSnapshot snap)
        {
            CapabilityInventory inv = CapabilityInventory.Build(snap, player, null);
            (UnitData unit, HexCoord hex, float upgrade, string stableKey)? best = null;
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (army?.Members == null)
                    continue;
                foreach (UnitData u in army.Members)
                {
                    if (u == null || u.IsAviation || u.Equipment != null)
                        continue;
                    if (!EquipmentSystem.CanAttach(equipCard, u, root, out _))
                        continue;
                    float delta = StrategicCardEvaluator.EquipmentUpgradeValue(
                        equipCard.Definition, u, army, snap, inv);
                    string stableKey = $"{army.Id}:{army.Members.IndexOf(u)}";
                    if (best == null || delta > best.Value.upgrade + 0.0001f
                        || (System.Math.Abs(delta - best.Value.upgrade) <= 0.0001f
                            && string.CompareOrdinal(stableKey, best.Value.stableKey) < 0))
                        best = (u, army.Hex, delta, stableKey);
                }
            }
            return best;
        }

        private static ResourceCost CombinedCost(ResourceCost a, ResourceCost b)
        {
            int h = (a?.human ?? 0) + (b?.human ?? 0);
            int e = (a?.energy ?? 0) + (b?.energy ?? 0);
            int m = (a?.materials ?? 0) + (b?.materials ?? 0);
            int t = (a?.tech ?? 0) + (b?.tech ?? 0);
            return (h | e | m | t) == 0
                ? null : new ResourceCost { human = h, energy = e, materials = m, tech = t };
        }

        private static void StampVersion(ref NonCombatExecuteResult result, int versionBefore)
        {
            if (result.StateChanged && V2StateVersion.Current == versionBefore)
                V2StateVersion.Bump();
        }
    }
}
