using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    public readonly struct PlacementOption
    {
        public readonly HexCoord Hex;
        public readonly DeploymentKind Kind;
        public readonly ArmyData Army;

        public PlacementOption(HexCoord hex, DeploymentKind kind, ArmyData army)
        {
            Hex = hex;
            Kind = kind;
            Army = army;
        }

        public CardPlayPlan Bind(CardData card) =>
            Kind == DeploymentKind.NewArmy
                ? CardPlayPlan.NewArmyAt(card, Hex)
                : CardPlayPlan.Into(card, Hex, Kind, Army);

        public string Key => $"{(int)Kind}:{Hex.Q},{Hex.R}:{(Army != null ? Army.Id : -1)}";
    }

    internal static class PlacementSelector
    {
        public static List<PlacementOption> BuildOptions(WorldSnapshot snap, PlayerSetupData player,
            CardDefinition def, ActorCommitments commitments, bool soloOnly)
        {
            var opts = new List<PlacementOption>();
            if (def == null || snap?.Self?.BaseHexes == null || player == null)
                return opts;

            bool isUnit = def.cardType == CardType.Unit;
            bool isHero = def.cardType == CardType.Hero;
            List<ArmyData> own = ArmyRegistry.AllForOwner(player).Where(a => a != null).OrderBy(a => a.Id).ToList();

            foreach (HexCoord hex in snap.Self.BaseHexes)
            {
                if (!PlacementRules.HasRequiredBuilding(player, hex, def))
                    continue;

                ArmyData shell = ReusableArmySelector.FindReusableAt(player, hex, commitments);
                if (shell != null)
                    opts.Add(new PlacementOption(hex, DeploymentKind.ReusableShell, shell));
                opts.Add(new PlacementOption(hex, DeploymentKind.NewArmy, null));

                if (soloOnly)
                    continue;

                foreach (ArmyData a in own)
                {
                    if (!a.Hex.Equals(hex) || a.IsPrison)
                        continue;
                    if (a.IsGarrison)
                    {
                        if (PlacementRules.CanDepositIntoGarrison(a)
                            && CardPlayExecutor.CanFitAfterDeploy(a, def))
                            opts.Add(new PlacementOption(hex, DeploymentKind.Garrison, a));
                        continue;
                    }
                    // Projected capacity, not pre-join HasRoom: a first hero may legally turn a
                    // full 2/2 body formation into 3/N and is exactly the placement a live Hero
                    // strategic demand needs. CardPlayExecutor/ArmyActions enforce the same rule.
                    if (a.Members.Count == 0 || !CardPlayExecutor.CanFitAfterDeploy(a, def))
                        continue;
                    // IsPlainReserveArmy intentionally means "heroless AND currently has room" for
                    // generic placement callers. A Hero card is the one exception here: it may lead
                    // a full heroless non-Recce ground formation when its projected CommandRating
                    // creates the needed slot. Keep that exception local to prospective Hero-card
                    // placement instead of weakening the global reserve-army predicate for Units.
                    bool heroCanLeadFullFormation = isHero
                        && !a.IsAirfield && !a.IsAirArmy
                        && !AbilityParams.ArmyHasAnyRecce(a)
                        && a.Members.All(u => u != null && !u.IsHero && !u.IsAviation);
                    bool ok = AiArmyRoles.IsPlainReserveArmy(a)
                        || heroCanLeadFullFormation
                        || (isUnit && AiArmyRoles.IsHeroLedCombatArmy(a));
                    if (ok)
                        opts.Add(new PlacementOption(hex, DeploymentKind.ExistingArmy, a));
                }
            }
            return opts;
        }
    }

    public sealed class MaterializationReservation
    {
        public readonly HashSet<string> ClaimedGeneratorUses = new HashSet<string>();
        public readonly HashSet<string> TriedGeneratorCards = new HashSet<string>();
        public readonly List<AxisDemand> UnresolvedDemands = new List<AxisDemand>();
        public int GenerationAttemptsUsed;

        public bool CanGenerateMore => GenerationAttemptsUsed < AiConfigV2.maxGenerationActionsPerTurn;

        public void RecordGenerationAttempt(GenerationStep g, MaterializationResult r)
        {
            if (g == null) return;
            GenerationAttemptsUsed++;
            if (!string.IsNullOrEmpty(g.UseKey)) ClaimedGeneratorUses.Add(g.UseKey);
            if (!string.IsNullOrEmpty(g.CardKey)) TriedGeneratorCards.Add(g.CardKey);
            if (r != null && !string.IsNullOrEmpty(r.AttemptedGenerationUseKey))
                ClaimedGeneratorUses.Add(r.AttemptedGenerationUseKey);
        }

        public AxisDemand BestUnresolvedDemandFor(MaterializationPlan plan)
        {
            if (plan == null) return null;
            return UnresolvedDemands
                .Where(d => d != null && d.DesiredAmount > 0f && d.Capability == plan.FinalCapability
                    && (plan.ExpectedTraits & d.RequiredTraits) == d.RequiredTraits)
                .OrderByDescending(d => d.Value)
                .ThenBy(d => (int)d.RequestingAxis)
                .FirstOrDefault();
        }
    }

    // AI-MGR-01 review-r3 — one demand's scored, opportunity-adjusted chain. DecisionScore
    // (Play - Hold + urgency) is computed ONCE here and carried all the way to the cross-demand
    // arbitration, so the manager never re-ranks on the raw play score again.
    public readonly struct DemandCandidate
    {
        public readonly MaterializationPlan Plan;
        public readonly float FollowupAp;
        public readonly float PlayScore;
        public readonly float HoldValue;
        public readonly float DecisionScore;

        public DemandCandidate(MaterializationPlan plan, float followupAp, float playScore,
            float holdValue, float decisionScore)
        {
            Plan = plan;
            FollowupAp = followupAp;
            PlayScore = playScore;
            HoldValue = holdValue;
            DecisionScore = decisionScore;
        }

        // Worth playing at all: holding the card (+ any urgency) does not beat it.
        public bool Worthwhile => DecisionScore > AiConfigV2.allocatorSliceEpsilon;
    }

    internal static class MaterializationCandidateBuilder
    {
        // AI-MGR-01 P1.3 — excludeCards / excludeGenKeys let the Phase A instance assignment ask
        // for the chains that AVOID a hand card / generation source another demand has claimed, so
        // two demands never both count one physical card as available capacity.
        public static List<DemandCandidate> TopForDemand(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand,
            AxisBudgetLedger ledger, ActorCommitments commitments, float reservedFollowupAp,
            MaterializationReservation reservation, CapabilityInventory inv, bool hasCompetingHeroDemand,
            int k = 3,
            System.Collections.Generic.ISet<CardData> excludeCards = null,
            System.Collections.Generic.ISet<string> excludeGenKeys = null)
        {
            float eps = AiConfigV2.allocatorSliceEpsilon;
            float axisBudget = ledger.DiscreteAdmissionBudget(demand.RequestingAxis);
            bool soloOnly = demand.Capability == CapabilityKind.ScoutCapability;
            int stealthSurcharge = (demand.RequiredTraits & TraitPreference.Stealth) != 0
                ? AiConfigV2.scoutOptionalStealthAp : 0;
            bool Excluded(CardData c) => c != null && excludeCards != null && excludeCards.Contains(c);
            bool ExcludedGen(GenerationStep g) => g != null && excludeGenKeys != null
                && !string.IsNullOrEmpty(g.CardKey) && excludeGenKeys.Contains(g.CardKey);

            List<CardData> handList = hand.Hand.ToList();
            List<GenerationStep> genSteps = reservation != null && reservation.CanGenerateMore
                ? GenerationSource.Enumerate(player, root, ctx, hand,
                    reservation.ClaimedGeneratorUses, reservation.TriedGeneratorCards)
                : new List<GenerationStep>();

            var candidates = new List<(MaterializationPlan plan, float followupAp, TraitPreference proj)>();

            for (int i = 0; i < handList.Count; i++)
            {
                CardData card = handList[i];
                CardDefinition def = card?.Definition;
                if (def == null || def.isAviation || Excluded(card) || !MaterializationChainMatching.MatchesCapabilityDef(def, demand.Capability))
                    continue;

                IReadOnlyList<string> baseAbilities = MaterializationChainMatching.EffectiveAbilities(def, card.Equipment);
                if (MaterializationChainMatching.AbilitiesSatisfyCapability(baseAbilities, def.cardType, demand.Capability)
                    && MaterializationChainMatching.MeetsRequiredTraits(baseAbilities, demand.RequiredTraits))
                {
                    foreach (PlacementOption opt in PlacementSelector.BuildOptions(snap, player, def, commitments, soloOnly))
                    {
                        if (!CardPlayExecutor.Preflight(player, root, hand, ctx, opt.Bind(card), out _))
                            continue;
                        MaterializationPlan p = MaterializationPlanFactory.MakeExistingPlan(MaterializationChainKind.Direct, demand,
                            card, i, null, -1, opt, baseAbilities);
                        AddIfFeasibleA(candidates, p, demand, def, stealthSurcharge, reservedFollowupAp,
                            axisBudget, eps, root, hand, player, ctx);
                    }
                }

                if (card.Equipment == null)
                {
                    for (int j = 0; j < handList.Count; j++)
                    {
                        if (j == i) continue;
                        CardData eq = handList[j];
                        CardDefinition eqDef = eq?.Definition;
                        if (eqDef == null || Excluded(eq) || eqDef.cardType != CardType.Equipment || eqDef.equipment == null
                            || !MaterializationChainMatching.EquipmentDefFitsHostDef(eqDef, def))
                            continue;
                        List<string> projected = EquipmentSystem.EffectiveAbilities(baseAbilities, eqDef.equipment);
                        if (!MaterializationChainMatching.AbilitiesSatisfyCapability(projected, def.cardType, demand.Capability)
                            || !MaterializationChainMatching.MeetsRequiredTraits(projected, demand.RequiredTraits))
                            continue;

                        foreach (PlacementOption opt in PlacementSelector.BuildOptions(snap, player, def, commitments, soloOnly))
                        {
                            if (!CardPlayExecutor.Preflight(player, root, hand, ctx, opt.Bind(card), out _))
                                continue;
                            MaterializationPlan p = MaterializationPlanFactory.MakeExistingPlan(MaterializationChainKind.AttachDeploy, demand,
                                card, i, eq, j, opt, projected);
                            AddIfFeasibleA(candidates, p, demand, def, stealthSurcharge, reservedFollowupAp,
                                axisBudget, eps, root, hand, player, ctx);
                        }
                    }
                }
            }

            foreach (GenerationStep g in genSteps)
            {
                if (ExcludedGen(g)) continue;
                CardDefinition gd = g.CardDef;
                if (g.ProducesEquipment)
                {
                    if (gd.equipment == null) continue;
                    for (int i = 0; i < handList.Count; i++)
                    {
                        CardData host = handList[i];
                        CardDefinition hd = host?.Definition;
                        if (hd == null || Excluded(host) || hd.isAviation || host.Equipment != null
                            || !MaterializationChainMatching.MatchesCapabilityDef(hd, demand.Capability) || !MaterializationChainMatching.EquipmentDefFitsHostDef(gd, hd))
                            continue;
                        IReadOnlyList<string> hostAbilities = MaterializationChainMatching.EffectiveAbilities(hd, null);
                        List<string> projected = EquipmentSystem.EffectiveAbilities(hostAbilities, gd.equipment);
                        if (!MaterializationChainMatching.AbilitiesSatisfyCapability(projected, hd.cardType, demand.Capability)
                            || !MaterializationChainMatching.MeetsRequiredTraits(projected, demand.RequiredTraits))
                            continue;

                        foreach (PlacementOption opt in PlacementSelector.BuildOptions(snap, player, hd, commitments, soloOnly))
                        {
                            MaterializationPlan p = MaterializationPlanFactory.MakeGeneratedPlan(MaterializationChainKind.GenerateAttachDeploy,
                                demand, g, baseInHand: host, baseIdx: i, generatedIsEquipment: true, opt: opt,
                                projected: projected);
                            AddIfFeasibleA(candidates, p, demand, hd, stealthSurcharge, reservedFollowupAp,
                                axisBudget, eps, root, hand, player, ctx);
                        }
                    }
                }
                else
                {
                    if ((gd.cardType != CardType.Unit && gd.cardType != CardType.Hero) || gd.isAviation
                        || !MaterializationChainMatching.MatchesCapabilityDef(gd, demand.Capability))
                        continue;
                    IReadOnlyList<string> genAbilities = MaterializationChainMatching.EffectiveAbilities(gd, null);
                    List<PlacementOption> genOpts = PlacementSelector.BuildOptions(snap, player, gd, commitments, soloOnly);
                    if (genOpts.Count == 0) continue;

                    if (MaterializationChainMatching.AbilitiesSatisfyCapability(genAbilities, gd.cardType, demand.Capability)
                        && MaterializationChainMatching.MeetsRequiredTraits(genAbilities, demand.RequiredTraits))
                    {
                        foreach (PlacementOption opt in genOpts)
                        {
                            MaterializationPlan p = MaterializationPlanFactory.MakeGeneratedPlan(MaterializationChainKind.GenerateDeploy,
                                demand, g, baseInHand: null, baseIdx: -1, generatedIsEquipment: false, opt: opt,
                                projected: genAbilities);
                            AddIfFeasibleA(candidates, p, demand, gd, stealthSurcharge, reservedFollowupAp,
                                axisBudget, eps, root, hand, player, ctx);
                        }
                    }

                    for (int j = 0; j < handList.Count; j++)
                    {
                        CardData eq = handList[j];
                        CardDefinition eqDef = eq?.Definition;
                        if (eqDef == null || Excluded(eq) || eqDef.cardType != CardType.Equipment || eqDef.equipment == null
                            || !MaterializationChainMatching.EquipmentDefFitsHostDef(eqDef, gd))
                            continue;
                        List<string> projected = EquipmentSystem.EffectiveAbilities(genAbilities, eqDef.equipment);
                        if (!MaterializationChainMatching.AbilitiesSatisfyCapability(projected, gd.cardType, demand.Capability)
                            || !MaterializationChainMatching.MeetsRequiredTraits(projected, demand.RequiredTraits))
                            continue;
                        foreach (PlacementOption opt in genOpts)
                        {
                            MaterializationPlan p = MaterializationPlanFactory.MakeGeneratedPlan(MaterializationChainKind.GenerateAttachDeploy,
                                demand, g, baseInHand: null, baseIdx: -1, generatedIsEquipment: false, opt: opt,
                                projected: projected, equipInHand: eq, equipIdx: j);
                            AddIfFeasibleA(candidates, p, demand, gd, stealthSurcharge, reservedFollowupAp,
                                axisBudget, eps, root, hand, player, ctx);
                        }
                    }
                }
            }

            if (candidates.Count == 0) return new List<DemandCandidate>();

            int referenceMoveMax = 0;
            if (demand.Capability == CapabilityKind.ScoutCapability)
            {
                var reference = candidates
                    .OrderBy(c => c.plan.ApCost + c.followupAp)
                    .ThenBy(c => ResourceCostSum(c.plan.ResCost))
                    .ThenBy(c => (int)c.plan.Kind)
                    .ThenBy(c => c.plan.StableKey, System.StringComparer.Ordinal)
                    .First();
                referenceMoveMax = CapabilityQualityEvaluator.ProjectedMoveMax(reference.plan);
            }

            foreach (var c in candidates)
                c.plan.Score = ScorePlanA(c.plan, demand, c.proj, inv, referenceMoveMax, hasCompetingHeroDemand, snap);

            // AI-MGR-01 P0 review-r3 — DecisionScore = Play - Hold + urgency, computed ONCE here.
            // Urgency (a function of demand.Value) is folded in so a real threat lifts every net
            // value; the cross-demand arbitration in StrategicManager ranks purely on DecisionScore
            // and never re-applies demand.Value or re-reads the raw play score.
            float urgency = UrgencyBonus(demand.Value);
            float Decide(MaterializationPlan p) =>
                p.Score - (p.UseBreakdown?.HoldValue ?? 0f) + urgency;

            var ranked = candidates
                .OrderByDescending(c => Decide(c.plan))
                .ThenByDescending(c => c.plan.Score)
                .ThenBy(c => c.plan.StableKey, System.StringComparer.Ordinal)
                .ToList();

            // AI-MGR-01 review-r4 finding 2 — the top-K cut is taken over unique CONSUMPTION
            // SIGNATURES (base card instance + equipment card instance + generation source), NOT raw
            // MaterializationPlans. One physical card yields many plans (A@army1, A@army2,
            // A@garrison, …); without this dedup those clones eat every K slot and a fallback card B
            // is lost before the injective assignment ever sees it (D1{A,B}+D2{A} would collapse to
            // D1{A,A,A}). `ranked` is best-first, so the first plan seen per signature is its best
            // placement.
            var dedup = new List<(MaterializationPlan plan, float followupAp, TraitPreference proj)>();
            var seenSig = new HashSet<string>();
            foreach (var c in ranked)
                if (seenSig.Add(ConsumptionSignature(c.plan)))
                    dedup.Add(c);

            LogQualityChoice(demand, dedup);
            MaterializationPlan best = dedup[0].plan;
            if (best.UseBreakdown != null)
                AiDebugLog.Write($"[AI][V2]   strat.eval A — {demand.Capability} via {best.StableKey} "
                    + $"role={best.UseRole} play {best.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"hold {(best.UseBreakdown?.HoldValue ?? 0f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"urgency {urgency.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"decision {Decide(best).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"[{best.UseBreakdown.ToCompact()}]");

            var outList = new List<DemandCandidate>(Mathf.Max(1, k));
            foreach (var c in dedup.Take(Mathf.Max(1, k)))
            {
                float hold = c.plan.UseBreakdown?.HoldValue ?? 0f;
                outList.Add(new DemandCandidate(c.plan, c.followupAp, c.plan.Score, hold, Decide(c.plan)));
            }
            return outList;
        }

        // AI-MGR-01 review-r4 finding 2 — the physical resources a chain consumes, WITHOUT the
        // deployment target. It is StableKey minus its trailing `Deploy.Key` segment: the leading
        // segments already encode chain kind, capability, base-card hand index, equipment hand index
        // and generation CardKey. Two placements of the same card share it; a different card copy or
        // a different generation source does not.
        private static string ConsumptionSignature(MaterializationPlan p)
        {
            string k = p?.StableKey ?? "";
            int lastBar = k.LastIndexOf('|');
            return lastBar >= 0 ? k.Substring(0, lastBar) : k;
        }

        private static void LogQualityChoice(AxisDemand demand,
            List<(MaterializationPlan plan, float followupAp, TraitPreference proj)> ranked)
        {
            if (demand.Capability != CapabilityKind.ScoutCapability || ranked.Count == 0) return;
            MaterializationPlan win = ranked[0].plan;
            (MaterializationPlan plan, float followupAp, TraitPreference proj)? runner = null;
            foreach (var c in ranked.Skip(1))
            {
                bool differentBody = c.plan.Kind != win.Kind
                    || !ReferenceEquals(c.plan.BaseCardInHand, win.BaseCardInHand)
                    || c.plan.GeneratedBaseDef != win.GeneratedBaseDef;
                bool close = win.Score - c.plan.Score <= AiConfigV2.scoutQualityLogRunnerUpMargin;
                if (differentBody || close) { runner = c; break; }
            }
            if (runner == null) return;

            string Name(MaterializationPlan p) =>
                (p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef)?.displayName ?? "?";
            string bdW = win.QualityBreakdown != null ? win.QualityBreakdown.ToCompact() : "-";
            string bdR = runner.Value.plan.QualityBreakdown != null
                ? runner.Value.plan.QualityBreakdown.ToCompact() : "-";
            AiDebugLog.Write($"[AI][V2]   strat.A quality {demand.Capability} — "
                + $"{Name(win)} score {win.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} "
                + $"[{bdW}] > {Name(runner.Value.plan)} "
                + $"{runner.Value.plan.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} [{bdR}]");
        }

        // AI-MGR-02 round 6 — the full set of PREFLIGHTED surplus materialization plans (each one
        // already passed CardPlayExecutor.Preflight + ReservesOkAfterChain). BestSurplus picks the
        // highest-DecisionScore among these; the reaction feasibility probe needs the WHOLE set so
        // it can find the genuinely CHEAPEST feasible plan, not just the best-scored one.
        internal static List<MaterializationPlan> EnumerateSurplusPlans(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            CapabilityInventory inv, ActorCommitments commitments, MaterializationReservation reservation)
        {
            List<CardData> handList = hand.Hand.ToList();
            var candidates = new List<MaterializationPlan>();

            for (int i = 0; i < handList.Count; i++)
            {
                CardData card = handList[i];
                CardDefinition def = card?.Definition;
                if (def == null || def.isAviation) continue;
                bool recce = AbilityParams.AbilitiesHaveAnyRecce(def.grantedAbilities);
                bool hero = def.cardType == CardType.Hero;
                if (!recce && def.cardType != CardType.Unit && !hero) continue;

                CapabilityKind cap = recce ? CapabilityKind.ScoutCapability
                    : hero ? CapabilityKind.Hero : CapabilityKind.FieldCombatPower;
                bool soloOnly = cap == CapabilityKind.ScoutCapability;
                IReadOnlyList<string> baseAbilities = MaterializationChainMatching.EffectiveAbilities(def, card.Equipment);

                // §2 — if this card is still strategically relevant to an unresolved capability
                // demand, Phase B may only spend it on a placement that would actually deliver
                // that capability. Otherwise no candidate is generated and the card stays in hand
                // until Phase A resolves the demand or it drops out of the unresolved set.
                AxisDemand strategicClaim = UnresolvedClaimFor(reservation, cap, baseAbilities);

                foreach (PlacementOption opt in PlacementSelector.BuildOptions(snap, player, def, commitments, soloOnly))
                {
                    if (!CardPlayExecutor.Preflight(player, root, hand, ctx, opt.Bind(card), out _)) continue;
                    MaterializationPlan direct = MaterializationPlanFactory.MakeExistingPlan(MaterializationChainKind.Direct, null,
                        card, i, null, -1, opt, baseAbilities);
                    direct.FinalCapability = cap;
                    if (strategicClaim != null && !CanDeliverDemandOperationally(direct, strategicClaim))
                        continue;
                    if (StrategicSpendability.ReservesOkAfterChain(root, ctx, direct, player))
                    {
                        direct.Score = SurplusUtility(snap, direct, inv, recce, hero, hand, baseAbilities);
                        candidates.Add(direct);
                    }

                    if (!AiConfigV2.surplusAllowAttach || card.Equipment != null) continue;
                    for (int j = 0; j < handList.Count; j++)
                    {
                        if (j == i) continue;
                        CardData eq = handList[j];
                        CardDefinition eqDef = eq?.Definition;
                        if (eqDef == null || eqDef.cardType != CardType.Equipment || eqDef.equipment == null
                            || !MaterializationChainMatching.EquipmentDefFitsHostDef(eqDef, def))
                            continue;
                        List<string> projected = EquipmentSystem.EffectiveAbilities(baseAbilities, eqDef.equipment);
                        if (!MaterializationChainMatching.AbilitiesSatisfyCapability(projected, def.cardType, cap)) continue;
                        MaterializationPlan att = MaterializationPlanFactory.MakeExistingPlan(MaterializationChainKind.AttachDeploy, null,
                            card, i, eq, j, opt, projected);
                        att.FinalCapability = cap;
                        // Same strategic-claim protection as Direct/GeneratedDeploy. An attached
                        // variant must not become a back door that burns a live Hero/Field card in
                        // a zero-delivery placement merely because the equipment raised utility.
                        if (strategicClaim != null && !CanDeliverDemandOperationally(att, strategicClaim))
                            continue;
                        if (!StrategicSpendability.ReservesOkAfterChain(root, ctx, att, player)) continue;
                        // P1.4 — the evaluator owns the attach-step penalty (ChainStepPenalty in
                        // ResourceEfficiency); no extra subtraction here.
                        att.Score = SurplusUtility(snap, att, inv, recce, hero, hand, projected);
                        candidates.Add(att);
                    }
                }
            }

            if (AiConfigV2.surplusAllowGeneration && reservation != null && reservation.CanGenerateMore)
            {
                foreach (GenerationStep g in GenerationSource.Enumerate(player, root, ctx, hand,
                    reservation.ClaimedGeneratorUses, reservation.TriedGeneratorCards))
                {
                    CardDefinition gd = g.CardDef;
                    if (gd == null || gd.isAviation)
                        continue;

                    if (g.ProducesEquipment)
                    {
                        if (gd.equipment == null || !hand.HasFreeSlot)
                            continue;

                        for (int i = 0; i < handList.Count; i++)
                        {
                            CardData host = handList[i];
                            CardDefinition hd = host?.Definition;
                            if (hd == null || hd.isAviation || host.Equipment != null
                                || (hd.cardType != CardType.Unit && hd.cardType != CardType.Hero)
                                || !MaterializationChainMatching.EquipmentDefFitsHostDef(gd, hd))
                                continue;

                            IReadOnlyList<string> hostAbilities = MaterializationChainMatching.EffectiveAbilities(hd, null);
                            List<string> projected = EquipmentSystem.EffectiveAbilities(hostAbilities, gd.equipment);
                            bool recce = AbilityParams.AbilitiesHaveAnyRecce(projected);
                            bool hero = hd.cardType == CardType.Hero;
                            CapabilityKind cap = recce ? CapabilityKind.ScoutCapability
                                : hero ? CapabilityKind.Hero : CapabilityKind.FieldCombatPower;
                            bool soloOnly = cap == CapabilityKind.ScoutCapability;
                            AxisDemand strategicClaim = UnresolvedClaimFor(reservation, cap, projected);

                            foreach (PlacementOption opt in PlacementSelector.BuildOptions(snap, player, hd, commitments, soloOnly))
                            {
                                MaterializationPlan genEq = MaterializationPlanFactory.MakeGeneratedPlan(
                                    MaterializationChainKind.GenerateAttachDeploy, null, g,
                                    baseInHand: host, baseIdx: i, generatedIsEquipment: true,
                                    opt: opt, projected: projected);
                                genEq.FinalCapability = cap;
                                if (strategicClaim != null && !CanDeliverDemandOperationally(genEq, strategicClaim))
                                    continue;
                                if (!StrategicSpendability.ReservesOkAfterChain(root, ctx, genEq, player))
                                    continue;

                                // P1.4 — the evaluator owns the generation + attach step penalties
                                // (ChainStepPenalty) AND the generation success-chance discount
                                // (Deployability). No re-application here.
                                genEq.Score = SurplusUtility(snap, genEq, inv, recce, hero, hand, projected);
                                candidates.Add(genEq);
                            }
                        }
                        continue;
                    }

                    if (gd.cardType != CardType.Unit && gd.cardType != CardType.Hero)
                        continue;
                    bool genRecce = AbilityParams.AbilitiesHaveAnyRecce(gd.grantedAbilities);
                    bool genHero = gd.cardType == CardType.Hero;
                    CapabilityKind genCap = genRecce ? CapabilityKind.ScoutCapability
                        : genHero ? CapabilityKind.Hero : CapabilityKind.FieldCombatPower;
                    bool genSoloOnly = genCap == CapabilityKind.ScoutCapability;
                    IReadOnlyList<string> genAbilities = MaterializationChainMatching.EffectiveAbilities(gd, null);
                    AxisDemand genStrategicClaim = UnresolvedClaimFor(reservation, genCap, genAbilities);

                    foreach (PlacementOption opt in PlacementSelector.BuildOptions(snap, player, gd, commitments, genSoloOnly))
                    {
                        MaterializationPlan gen = MaterializationPlanFactory.MakeGeneratedPlan(MaterializationChainKind.GenerateDeploy,
                            null, g, baseInHand: null, baseIdx: -1, generatedIsEquipment: false, opt: opt,
                            projected: genAbilities);
                        gen.FinalCapability = genCap;
                        if (genStrategicClaim != null && !CanDeliverDemandOperationally(gen, genStrategicClaim))
                            continue;
                        if (gen.HandSlotsNeededAtPeak > 0 && !hand.HasFreeSlot) continue;
                        if (!StrategicSpendability.ReservesOkAfterChain(root, ctx, gen, player)) continue;
                        // P1.4 — evaluator owns the generation step penalty + success-chance discount.
                        gen.Score = SurplusUtility(snap, gen, inv, genRecce, genHero, hand, genAbilities);
                        candidates.Add(gen);
                    }
                }
            }

            return candidates;
        }

        public static (MaterializationPlan plan, float utility)? BestSurplus(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            CapabilityInventory inv, ActorCommitments commitments, MaterializationReservation reservation)
        {
            List<MaterializationPlan> candidates = EnumerateSurplusPlans(
                snap, player, root, hand, ctx, inv, commitments, reservation);
            if (candidates.Count == 0) return null;

            // final closure follow-up §P1 — GLOBAL highest-score arbitration, no residual bucket
            // ordering. Each candidate's Phase-B decision score is its NetScore plus the urgency of
            // the unresolved demand it would OPERATIONALLY deliver (the same UrgencyBonus ramp Phase
            // A folds into its DecisionScore). A residual candidate no longer skips ahead of a
            // higher-scored normal one — it just carries the weight its demand.Value earns. The
            // returned utility IS this decision score, so StrategicManager compares it directly with
            // the non-combat lane and never re-adds urgency.
            float DecisionScore(MaterializationPlan p)
            {
                AxisDemand d = reservation?.BestUnresolvedDemandFor(p);
                float urgency = d != null && CanDeliverDemandOperationally(p, d)
                    ? UrgencyBonus(d.Value) : 0f;
                return p.Score + urgency;
            }

            MaterializationPlan bestPlan = candidates
                .OrderByDescending(DecisionScore)
                .ThenByDescending(p => p.Score)
                .ThenBy(p => p.StableKey, System.StringComparer.Ordinal)
                .First();
            float bestDecision = DecisionScore(bestPlan);
            if (bestPlan.UseBreakdown != null)
                AiDebugLog.Write($"[AI][V2]   strat.eval B — {bestPlan.StableKey} role={bestPlan.UseRole} "
                    + $"net {bestPlan.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"decision {bestDecision.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"[{bestPlan.UseBreakdown.ToCompact()}]");
            return (bestPlan, bestDecision);
        }




        private static void AddIfFeasibleA(
            List<(MaterializationPlan plan, float followupAp, TraitPreference proj)> sink,
            MaterializationPlan p, AxisDemand demand, CardDefinition baseDef, int stealthSurcharge,
            float reservedFollowupAp, float axisBudget, float eps, PlayerRoot root, AiHandData hand,
            PlayerSetupData player, AiTurnContext ctx)
        {
            // Operational shortages may not spend a card on a placement whose live capability delta
            // is known in advance to be zero. Garrison placement is preparation, not Field/Hero
            // delivery; a solo Hero shell/new army is likewise reserve-only until it has an escort.
            if (!CanDeliverDemandOperationally(p, demand))
                return;

            float activationAp = p != null
                ? CapabilityQualityEvaluator.ProjectedActivationApCost(p)
                : (baseDef != null ? baseDef.activationApCost : AiConfigV2.scoutNotionalActivationAp);
            float followupAp = activationAp + stealthSurcharge + demand.MinimumFollowupAp;
            float need = p.ApCost + reservedFollowupAp + followupAp;
            if (need > axisBudget + eps) return;
            if (root.ActionPoints - need - AiConfigV2.housekeepingApReserve < -eps) return;
            if (!ChainResourcesAffordable(root, player, ctx, p.ResCost)) return;
            if (p.HandSlotsNeededAtPeak > 0 && !hand.HasFreeSlot) return;
            sink.Add((p, followupAp, p.ExpectedTraits));
        }

        // §2 — the best still-unresolved strategic demand this surplus card would be relevant to,
        // or null. A non-null result means Phase B must not spend the card on a placement that
        // cannot operationally deliver `cap`; it is held in hand instead.
        internal static AxisDemand UnresolvedClaimFor(MaterializationReservation reservation,
            CapabilityKind cap, IReadOnlyList<string> projectedAbilities)
        {
            if (reservation == null || reservation.UnresolvedDemands.Count == 0)
                return null;
            TraitPreference projTraits = MaterializationChainMatching.TraitsOf(projectedAbilities);
            return reservation.UnresolvedDemands
                .Where(d => d != null && d.DesiredAmount > 0f && d.Capability == cap
                    && (projTraits & d.RequiredTraits) == d.RequiredTraits)
                .OrderByDescending(d => d.Value)
                .ThenBy(d => (int)d.RequestingAxis)
                .FirstOrDefault();
        }

        // ARCH-02 §16 — moved to the canonical MaterializationDeliveryPolicy. This forwarder keeps
        // the widely-used name for the builder's own gates and its external callers.
        internal static bool CanDeliverDemandOperationally(MaterializationPlan p, AxisDemand demand)
            => MaterializationDeliveryPolicy.CanDeliverDemandOperationally(p, demand);

        // ARCH-02 §45 — route through the one owner-aware spendability seam so a bounded reaction
        // envelope (or any other explicit reservation) is respected here too, not just the legacy
        // recon-air pool.
        private static bool ChainResourcesAffordable(PlayerRoot root, PlayerSetupData player,
            AiTurnContext ctx, ResourceCost cost)
        {
            if (cost == null) return true;
            foreach (Game.Economy.ResourceType t in ResourceBundle.All)
                if (StrategicSpendability.SpendableAmount(player, root, ctx, t) < cost.Get(t)) return false;
            return true;
        }

        // AI-MGR-01 — Phase A scoring is now the shared StrategicCardEvaluator (Card x IntendedUse,
        // BaselineForceReadiness, no flat Hero bonus). This wrapper keeps the call signature and
        // still carries the Scout capability-quality breakdown + the new use breakdown for logging.
        private static float ScorePlanA(MaterializationPlan p, AxisDemand demand, TraitPreference projected,
            CapabilityInventory inv, int referenceMoveMax, bool hasCompetingHeroDemand, WorldSnapshot snap)
        {
            StrategicCardUseCandidate cand = StrategicCardEvaluator.ScoreForDemand(
                p, demand, projected, inv, referenceMoveMax, hasCompetingHeroDemand, snap);
            p.QualityBreakdown = cand.QualityBreakdown;
            p.UseBreakdown = cand.Breakdown;
            p.UseRole = cand.IntendedRole;
            return cand.TotalUseScore;
        }

        private static float ResourceCostSum(ResourceCost c) => c == null
            ? 0f : c.human + c.energy + c.materials + c.tech;

        // AI-MGR-01 P0.2 review-r2 — urgency enters the Play-vs-Hold equation (not a hard switch):
        // a demand's Value ramps a bonus added to every candidate's net decision value, so a real
        // threat / raid gap keeps materialising even against a card with a high HoldValue, while a
        // soft baseline demand adds ~nothing and can genuinely lose to Hold.
        // Shared Play-vs-Hold / Phase-B urgency ramp off a demand's Value. Used by Phase A's
        // DecisionScore and (final closure follow-up §P1) by BestSurplus's global decision score so
        // an operational residual competes on score instead of a hard boolean priority.
        private static float UrgencyBonus(float demandValue)
        {
            float t = Mathf.Clamp01((demandValue - AiConfigV2.stratHoldUrgencyRampLo)
                / Mathf.Max(0.01f, AiConfigV2.stratHoldUrgencyRampHi - AiConfigV2.stratHoldUrgencyRampLo));
            return t * AiConfigV2.stratHoldUrgencyMax;
        }

        // AI-MGR-01 — Phase B surplus scoring is the shared StrategicCardEvaluator too. It builds a
        // Card x IntendedRole candidate set and returns the best NetScore (play value minus the
        // separately scored HoldValue).
        private static float SurplusUtility(WorldSnapshot snap, MaterializationPlan p, CapabilityInventory inv,
            bool recce, bool hero, AiHandData hand, IReadOnlyList<string> projected)
        {
            StrategicCardUseCandidate cand = StrategicCardEvaluator.ScoreSurplus(
                p, inv, recce, hero, hand, projected, snap);
            p.UseBreakdown = cand.Breakdown;
            p.UseRole = cand.IntendedRole;
            return cand.NetScore;
        }






    }
}
