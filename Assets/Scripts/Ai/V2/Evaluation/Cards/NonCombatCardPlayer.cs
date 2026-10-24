using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
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
            public CardData EquipHostCard;
            public float Score;
            public float ApCost;
            public ResourceCost ResCost;
            public string StableKey;
            public string Explain;
            // Set when this play must first MINT its card through a
            // Research/Production Challenge (Card is then a throwaway pre-mint stand-in;
            // NonCombatPlayExecutor.Execute re-resolves the placement against the real minted instance). null => Card is a real
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
                AiDebugLog.Write($"[AI][V2]   strat.nonCombat - {card?.Definition?.displayName} "
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
            AiTurnContext ctx, GenerationStep source = null)
        {
            if (card == null || !card.isAviation || operatorHero == null || snap == null
                || player == null || root == null || hand?.Hand == null || ctx == null
                || string.IsNullOrWhiteSpace(card.authoredKey))
                return float.NegativeInfinity;
            var generation = source ?? new GenerationStep
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

        // Fate forecasts use the same real airfield and known strike service as deployment.
        // This read-only context contains only the map/turn consumed by sortie projection.
        internal static float OperatorAviationOutputValue(WorldSnapshot snap, GenerationStep source)
        {
            var card = source?.CardDef;
            var player = snap?.Observer;
            var root = PlayerRootRegistry.FindFor(player);
            if (card?.isAviation != true || snap?.Map == null || root == null) return 0f;
            var ctx = new AiTurnContext { Map = snap.Map, TurnNumber = snap.TurnNumber };
            var objectives = ReconObjectiveEvaluator.Enumerate(snap).Where(o => o != null).ToList();
            var targets = KnownStrikeTargets(snap, player, root, ctx, objectives);
            var projected = ProjectedAircraft(card, player);
            targets = GroundCombatAirSupport.UncoveredMissileStock(snap, projected, targets,
                snap.Self?.Hand, snap.Map);
            var minted = new CardData(card) { ResearchProductionCreated = true };
            var fields = PlacementRules.EnumerateAviationPlacements(snap, player, root, minted,
                out _, requireCurrentAp: false);
            float best = 0f;
            foreach (var field in fields)
            {
                var service = AirfieldServiceTaskScore(snap, player, ctx, projected, field,
                    objectives, targets, false, out _, out _, out string use);
                if (use != null) best = Mathf.Max(best, OperatorAviationServiceValue(snap, source, service));
            }
            return best;
        }

        // Successful-output utility on the same scale as real production. The operator portfolio
        // applies success probability, attempt cost and success-contingent deployment cost later.
        internal static float OperatorAviationServiceValue(WorldSnapshot snap, GenerationStep source,
            TaskScore service)
        {
            var card = ResearchProductionSystem.MintCard(source.CardDef);
            var successful = StrategicCardEvaluator.ScoreNonCombat(NonCombatRole.Aviation,
                card, snap, null, null, 0f, actualApCost: 0f, actualResourceCost: new ResourceCost(),
                player: snap.Observer, operationalTask: service);
            return Mathf.Max(0f, successful.NetScore) * StrategicCardEvaluator.ProductionNeedScale(source, snap);
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

        // Every LEGAL non-combat play for the current hand (each already
        // resolved to a real placement / host / airfield slot / base slot by BuildPlayFor).
        // Phase-B arbitration and reaction probes consume the whole set
        // so it can find the genuinely CHEAPEST feasible reaction, not the best-scored card.
        internal static IEnumerable<NonCombatPlay> EnumeratePlays(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, List<string> blocked,
            MaterializationReservation reservation = null, float? witnessedUsefulApDemand = null,
            bool ignoreApForMeasurement = false)
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
                    && def.cardType != CardType.Equipment
                    && (def.cardType == CardType.Unit || def.cardType == CardType.Hero
                        || AbilityParams.AbilitiesHaveAnyRecce(def.grantedAbilities)))
                    continue;
                if (def.isAviation)
                {
                    foreach (NonCombatPlay aviation in BuildAviationPlays(card, null, snap,
                        player, root, hand, ctx, blocked, witnessedUsefulApDemand, ignoreApForMeasurement))
                        yield return aviation;
                    continue;
                }
                if (def.cardType == CardType.Equipment && def.equipment != null)
                {
                    // Keep every recipient alternative without first scoring the same set to
                    // select one. Generated attachments belong to materialization below.
                    foreach (var attachment in BuildEquipmentPlays(card, snap, player, root, hand, ctx,
                        card.EffectivePlayApCost, StrategicCardEvaluator.AddResourceCosts(card.EffectivePlayResourceCost, null),
                        witnessedUsefulApDemand, ignoreApForMeasurement))
                        yield return attachment;
                    continue;
                }
                NonCombatPlay p = BuildPlayFor(card, generation: null, snap, player, root, hand, ctx,
                    ownBaseHexes, blocked, witnessedUsefulApDemand, ignoreApForMeasurement);
                if (p != null)
                    yield return p;
            }

            // Generated non-combat cards. A Research/Production
            // Challenge whose minted card is an Aviation / Base / Facility is scored on the SAME
            // NetScore band (throwaway pre-mint stand-in), discounted by the Challenge success
            // chance + the generation step penalty; NonCombatPlayExecutor.Execute mints then deploys
            // via the canonical API. Generated Equipment stays with the materialization GenerateAttachDeploy chain.
            if (!ignoreApForMeasurement && reservation != null && reservation.CanGenerateMore)
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
                ? ResearchProductionSystem.AttemptApCost(generation.ChallengeCard) : 0f);
            ResourceCost totalRes = StrategicCardEvaluator.AddResourceCosts(card.EffectivePlayResourceCost,
                generation?.ChallengeCard?.resourceCost);
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
                            requireCardInHand: generation == null, requireCurrentAp: !investmentPreview))
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
                ? ResearchProductionSystem.AttemptApCost(generation.ChallengeCard) : 0f);
            ResourceCost totalRes = StrategicCardEvaluator.AddResourceCosts(card.EffectivePlayResourceCost,
                generation?.ChallengeCard?.resourceCost);
            int handOrdinal = hand.Hand.IndexOf(card);
            string sourceKey = generation != null
                ? "gen:" + generation.CardKey
                : $"hand:{handOrdinal}:{def.authoredKey ?? "?"}";
            List<ReconObjective> objectives = ReconObjectiveEvaluator.Enumerate(snap)
                .Where(o => o != null).ToList();

            // An aircraft is played only for a real use: a Recon target it adds service to (below)
            // or a ground-combat target it can strike, AND launch Energy it can actually sustain.
            // Without either the card is not a candidate at all - no flat aviation RoleFit is
            // earned for a wing with nothing to fly at, or one it could never launch. Without a map
            // (bare harness) there is nothing to judge targets against, so nothing is filtered.
            bool judgeUse = ctx?.Map != null;
            if (judgeUse)
            {
                float energyAfterPlay = StrategicSpendability.SpendableAmount(
                        player, root, ctx, Game.Economy.ResourceType.Energy)
                    - (totalRes?.energy ?? 0);
                if (!AviationSortieReservationEvaluator.CanSustainLaunch(
                        player, ctx.Map, energyAfterPlay, Mathf.Max(0, def.launchEnergyCost)))
                {
                    blocked?.Add($"{def.displayName}:aviation(launch_energy_unsustainable)");
                    return result;
                }
            }

            // A new aircraft is worth the air jobs it ADDS, not the ones the aircraft already in
            // play cover. The canonical air-capacity witness (ReconAssignmentPlanner.
            // MeasureAirCapacity — the same route/progress rules Assignment binds a sortie with)
            // says how many runnable AirSweep jobs existing wings and hangars already serve, within
            // the per-turn air-actor ceiling. When nothing is left uncovered the card carries no
            // operational task value.
            int uncoveredAirJobs = int.MaxValue;
            if (judgeUse)
            {
                int airJobs = objectives.Count(o => o.BaseValue > 0f
                    && ReconAirCapacityPolicy.IsAirServiceable(o));
                (int airborne, int spare) = ReconAssignmentPlanner.MeasureAirCapacity(
                    ctx, player, root, snap, objectives);
                uncoveredAirJobs = Mathf.Min(airJobs, ReconAirCapacityPolicy.MaxAirReconActorsPerTurn)
                    - (airborne + spare);
            }
            List<CombatAirSupportRequest> strikeTargets = judgeUse && ExplosionAttack.AttackPower(def.attack, def.grantedAbilities) > 0
                ? KnownStrikeTargets(snap, player, root, ctx, objectives) : null;
            List<UnitData> projected = ProjectedAircraft(def, player);

            if (strikeTargets != null)
                strikeTargets = GroundCombatAirSupport.UncoveredMissileStock(snap, projected,
                    strikeTargets, hand.Hand, ctx.Map, card);

            foreach (HexCoord airfield in airfields)
            {
                TaskScore service = AirfieldServiceTaskScore(snap, player, ctx, projected, airfield,
                    objectives, strikeTargets, uncoveredAirJobs > 0 && !ExplosionAttack.IsSingleUseUnit(def),
                    out int coverage, out string witness, out string use);
                if (judgeUse && use == null)
                {
                    AiDebugLog.Write($"[AI][V2][Aviation][Deployment] card={def.displayName} "
                        + $"airfield=({airfield.Q},{airfield.R}) decision=SKIP reason=no_recon_or_strike_target "
                        + $"uncoveredAirJobs={uncoveredAirJobs} witness={witness ?? "none"}");
                    continue;
                }
                float score = Score(snap, player, root, ctx, PlayKind.Aviation, card, hand, 0f,
                    totalAp, totalRes, generation, witnessedUsefulApDemand,
                    logDynamicEffect: true, operationalTask: service);
                int free = AiAirSortiePlanner.FreeLandingCapacity(airfield, player);
                AiDebugLog.Write($"[AI][V2][Aviation][Deployment] card={def.displayName} "
                    + $"airfield=({airfield.Q},{airfield.R}) capacity={free} "
                    + $"objectiveCoverage={coverage} uncoveredAirJobs={uncoveredAirJobs} task={service.Value:0.00} "
                    + $"decision=CANDIDATE use={use ?? "unjudged"} witness={witness ?? "none"}");
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
            if (judgeUse && result.Count == 0)
                blocked?.Add($"{def.displayName}:aviation(no_recon_or_strike_target)");
            return result;
        }

        // The ONE per-airfield service of an aircraft group: the best runnable Recon objective
        // (when Recon still has an uncovered air job) or the best uncovered ground-combat strike
        // it could fly from this airfield. A card's deployment and a stored aircraft's rebase
        // (AviationRebasePlanner) both read it, so a carrier moving with an army is valued as the
        // airfield to act from by both lanes. `use`: "recon", "strike@(q,r)" or null (no service).
        internal static TaskScore AirfieldServiceTaskScore(WorldSnapshot snap, PlayerSetupData player,
            AiTurnContext ctx, IReadOnlyList<UnitData> projected, HexCoord airfield,
            IReadOnlyList<ReconObjective> objectives, IReadOnlyList<CombatAirSupportRequest> strikeTargets,
            bool reconUseful, out int coverage, out string witness, out string use)
        {
            TaskScore service = reconUseful
                ? BestAirfieldServiceTaskScore(snap, player, ctx, projected, airfield, objectives,
                    out coverage, out witness)
                : NoMarginalAirService(out coverage, out witness);
            use = coverage > 0 ? "recon" : null;
            foreach (CombatAirSupportRequest target in strikeTargets
                ?? (IReadOnlyList<CombatAirSupportRequest>)System.Array.Empty<CombatAirSupportRequest>())
            {
                var combat = GroundCombatAirSupport.ProjectService(snap, player, ctx.Map,
                    projected, airfield, target);
                if (!combat.HasValue || combat.Value.Score.Value <= AiConfigV2.allocatorSliceEpsilon
                    || (use != null && combat.Value.Score.Value <= service.Value)) continue;
                service = combat.Value.Score;
                coverage = 1;
                witness = $"{target.Identity}; first strike in {combat.Value.FirstStrikeEta} turn(s)";
                use = $"{(target.IsStockPreparation ? "missile-stock" : "strike")}@({target.Target.Q},{target.Target.R})";
            }
            return service;
        }

        // Executable support plus honest missile-preparation witnesses. Formation still reads
        // only CombatSupportTargets; a reserve witness never owns or launches an attack.
        // Inventory is filtered at production/deployment, not rebase (self must not hide demand).
        internal static List<CombatAirSupportRequest> KnownStrikeTargets(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            IReadOnlyList<ReconObjective> objectives)
        {
            List<MissionIntent> intents = WorldSnapshot.IntentsOf(snap)
                .Where(i => i != null && i.Status == IntentStatus.Active).ToList();
            ActorCommitments commitments = ActorCommitments.FromIntents(intents, snap, objectives);
            return GroundCombatAirSupport.UncoveredRequests(snap, player, root, ctx,
                AviationRebasePlanner.CombatSupportTargets(snap, intents), commitments)
                .Concat(GroundCombatAirSupport.MissileStockTargets(snap)).ToList();
        }

        // The not-yet-deployed aircraft as the sortie planners see it. It carries the card's
        // granted abilities (UnitData.Abilities is filled from them at spawn), so its recce
        // radius - and with it the vision the launch rule and the route score read - is the one
        // the deployed aircraft will have when Assignment evaluates it.
        internal static List<UnitData> ProjectedAircraft(CardDefinition def, PlayerSetupData player)
        {
            var aircraft = new UnitData
            {
                Owner = player, IsAviation = true, OriginatingCard = def,
                Defense = def.defenseRating, HitPointsMax = def.hitPoints, HitPointsCurrent = def.hitPoints,
                Initiative = def.initiative, Range = def.range,
                MoveMax = Mathf.Max(1, def.moveMax),
                MoveCurrent = Mathf.Max(1, def.moveMax),
                Attack = Mathf.Max(0, def.attack),
                ActivationApCost = Mathf.Max(0, def.activationApCost),
                LaunchEnergyCost = Mathf.Max(0, def.launchEnergyCost),
                TurnsWithoutRefuel = Mathf.Max(0, def.turnsWithoutRefuel),
            };
            if (def.unitTypeTags != null) aircraft.TypeTags.UnionWith(def.unitTypeTags);
            if (def.grantedAbilities != null)
                aircraft.Abilities.UnionWith(def.grantedAbilities);
            return new List<UnitData> { aircraft };
        }

        private static TaskScore NoMarginalAirService(out int coverage, out string witness)
        {
            coverage = 0;
            witness = "covered_by_existing_air";
            return default;
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
            return BestAirfieldServiceTaskScore(snap, player, ctx, ProjectedAircraft(def, player), airfield,
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
            // A fixed airfield is threatened as its Base/Citadel; a mobile one as its carrier's army.
            bool fixedAirfield = snap?.Self?.BaseHexes?.Contains(airfield) == true;
            float airfieldThreat = snap?.Threat?.Threats?
                .Where(t => t?.Asset != null && t.Asset.Hex.Equals(airfield)
                    && (fixedAirfield
                        ? t.Asset.Kind == AssetKind.Citadel || t.Asset.Kind == AssetKind.Base
                        : t.Asset.Kind == AssetKind.Army))
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
                    AviationRange.FirstTurnOutboundBudget(projected));
                if (sweepPoint.Equals(airfield))
                    continue;
                Sortie? sameTurn = AiAirSortiePlanner.TryPlanSortieFromStorage(
                    airfield, projected, sweepPoint, ctx.Map, player);
                MultiTurnSortie? multiTurn = sameTurn.HasValue ? null
                    : AiAirSortiePlanner.TryPlanMultiTurnSortieFromStorage(
                        airfield, projected, sweepPoint, ctx.Map, player);
                if (!sameTurn.HasValue && !multiTurn.HasValue)
                    continue;
                if (!LaunchServesObjective(snap, player, ctx, projected, airfield, objective.FocusHex))
                    continue;
                coverage++;
                int eta = sameTurn.HasValue ? 1 : Mathf.Max(1, multiTurn.Value.RequiredTurns);
                int routeCost = sameTurn.HasValue
                    ? sameTurn.Value.TotalCost : multiTurn.Value.TotalRouteCost;
                int moveMax = projected.Select(AviationRules.EffectiveMoveMax)
                    .DefaultIfEmpty(1).Min();
                int activationAp = projected.Sum(u => Mathf.Max(0, u.ActivationApCost));
                int launchEnergy = projected.Sum(u => Mathf.Max(0, u.LaunchEnergyCost));
                TaskScore candidate = CopyReconScoreWithAirDelivery(snap, objective.TaskScore,
                    eta, routeCost, moveMax, activationAp, launchEnergy,
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

        // The launch rule Assignment and air capacity bind a hangar sortie with
        // (ReconAirReservationPrepass.EvaluateAirStructuralFeasibility's storage branch +
        // ReconAssignmentPlanner's MakesGenuineProgress re-check): the first step PickFromStorage
        // chooses toward this objective must clear MinimumUsefulScore and progress toward it.
        // A proven round trip alone is not service — a card or rebase valued on a sortie the
        // launch rule then refuses (low route score, sector already covered) never flies.
        private static bool LaunchServesObjective(WorldSnapshot snap, PlayerSetupData player,
            AiTurnContext ctx, IReadOnlyList<UnitData> projected, HexCoord airfield, HexCoord focus)
        {
            ReconMode mode = AirReconModePolicy.RequestedMode(player, snap);
            ReconAirStepPlanner.StepChoice? step = ReconAirStepPlanner.PickFromStorage(
                player, ctx, airfield, projected, snap, mode,
                ctx.TurnNumber, missionFocusHex: focus);
            if (!step.HasValue || step.Value.Score < ReconAirStepPlanner.MinimumUsefulScore)
                return false;
            int vision = (ctx.GameConfig != null ? ctx.GameConfig.armyVisionRadius : 0)
                + projected.Select(AbilityParams.GetBestRecceRadius).DefaultIfEmpty(0).Max();
            return ReconAirStepPlanner.MakesGenuineProgress(airfield, step.Value.Hex, focus, vision);
        }

        private static TaskScore CopyReconScoreWithAirDelivery(WorldSnapshot snap, TaskScore s,
            int eta, int routeCost, int moveMax, int activationAp, int launchEnergy,
            float airfieldThreatRisk)
        {
            float movementShare = Mathf.Clamp01(Mathf.Max(0, routeCost)
                / (float)Mathf.Max(1, moveMax * Mathf.Max(1, eta)));
            // The share of this turn's activation the sortie uses is the AP it spends now — a
            // CardPrice fact of the aircraft, not the value of a task taken from it. These
            // aircraft replace the objective's own sortie price (notional or another wing's):
            // execution slots are the serving actor's, never summed with a different actor's.
            float sortieNow = TaskScoreEvaluator.Price(movementShare * activationAp
                + ActionPrice.Resources(t => t == Game.Economy.ResourceType.Energy
                    ? launchEnergy : 0f, snap));
            TaskScore sortie = TaskScoreEvaluator.WithExecution(s, new TaskScore(
                winChance: s.WinChance,
                // Airborne continuation is free. ETA remains part of the route projection above;
                // it must not turn a single sortie launch into recurring ground activation.
                cardPrice: sortieNow));
            // The airfield the sortie launches from is part of this task's own exposure.
            return TaskScore.FromSlots(slot => slot == TaskSlot.HexThreatRisk
                ? sortie.HexThreatRisk + airfieldThreatRisk : sortie[slot]);
        }

        // The placement of a just-generated card against the live world (the pre-mint stand-in's
        // target may be stale): the same BuildPlayFor rule, for NonCombatPlayExecutor.
        internal static NonCombatPlay PlayForGeneratedCard(CardData minted, WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx) =>
            BuildPlayFor(minted, generation: null, snap, player, root, hand, ctx,
                OwnedBaseHexes(snap, player), new List<string>());

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
            foreach (BuildingData b in OwnWorld.Of(player).Buildings)
                if (b.IsBase)
                    set.Add(b.Hex);
            if (snap?.Self?.BaseHexes != null)
                foreach (HexCoord h in snap.Self.BaseHexes)
                    set.Add(h);
            if (snap?.Self != null)
                set.Add(snap.Self.Citadel);
            return set.OrderBy(h => h.Q).ThenBy(h => h.R).ToList();
        }

        private static IEnumerable<NonCombatPlay> BuildEquipmentPlays(CardData card,
            WorldSnapshot snap, PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            float ap, ResourceCost resources, float? workload, bool ignoreApForMeasurement = false)
        {
            CapabilityInventory inv = CapabilityInventory.Build(snap, player, null);
            foreach (DevelopmentOpportunity op in DevelopmentOpportunityEvaluator.EquipmentOpportunities(
                ResearchProductionMode.Production, default, card.Definition, 1f, null,
                snap, inv, player, root, hand, out _, card, ignoreApForMeasurement: ignoreApForMeasurement))
            {
                // The candidate is the actual card in hand; a reservation on its recipient is
                // respected through the same bank/physical portfolio, and installation is rechecked.
                if (op.RecipientCard != null
                    ? !(ignoreApForMeasurement
                        ? EquipmentSystem.CanAttachPreview(card.Definition, op.RecipientCard, out _)
                        : EquipmentSystem.CanAttach(card, op.RecipientCard, root, out _))
                    : !(ignoreApForMeasurement
                        ? EquipmentSystem.CanAttachPreview(card.Definition, op.RecipientUnit, out _)
                        : EquipmentSystem.CanAttach(card, op.RecipientUnit, root, out _))) continue;
                // The item's own utility, without production context (no Challenge, repeat or supply).
                float value = EquipmentUpgradeValuation.EquipmentUpgradeValue(op);
                yield return new NonCombatPlay
                {
                    Card = card, Kind = PlayKind.Equipment, EquipHost = op.RecipientUnit,
                    EquipHostCard = op.RecipientCard,
                    TargetHex = op.RecipientUnit != null
                        ? OwnWorld.Of(player).ArmyContaining(op.RecipientUnit)?.Hex ?? default : default,
                    ApCost = ap, ResCost = resources,
                    Score = Score(snap, player, root, ctx, PlayKind.Equipment, card, hand, value,
                        ap, resources, null, workload),
                    StableKey = "hand:" + GenerationSource.StableCardKey(card)
                        + ":equipment:" + DevelopmentOpportunityEvaluator.RecipientKey(op),
                    Explain = card.Definition.displayName + " -> " + op.RecipientLabel
                        + " purpose=" + EquipmentUpgradeValuation.EquipmentPurposeLabel(snap, op.RecipientCard, op.RecipientUnit)
                        + $" delta={op.ExpectedGain:0.###} tactical={op.TacticalGain:0.###} " + op.Explain,
                };
            }
        }

    }
}
