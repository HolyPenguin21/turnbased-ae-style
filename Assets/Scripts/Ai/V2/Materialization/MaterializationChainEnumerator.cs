using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ARCH-02 §9/§10 / DoD "Enumeration отделена от feasibility ... от scoring" — the chain
    // enumerator. It owns ONLY the set of legal chain shapes (Direct / Attach->Deploy /
    // Generate->Deploy / Generate->Attach->Deploy): it consults MaterializationChainMatching for
    // relevance, PlacementSelector for placements, CardPlayExecutor.Preflight for legality,
    // MaterializationPlanFactory for construction and MaterializationFeasibility for the Phase-A
    // gate. It never scores a plan and never picks a "best" one — MaterializationCandidateBuilder
    // does that with the canonical StrategicCardEvaluator. Bodies verbatim from the builder.
    internal static class MaterializationChainEnumerator
    {
        // Phase A — every feasible chain for one capability demand (score-free).
        internal static List<(MaterializationPlan plan, float followupAp, TraitPreference proj)> EnumerateForDemand(
            WorldSnapshot snap, PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            AxisDemand demand, AxisBudgetLedger ledger, ActorCommitments commitments, float reservedFollowupAp,
            MaterializationReservation reservation,
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
                        MaterializationFeasibility.AddIfFeasibleA(candidates, p, demand, def, stealthSurcharge, reservedFollowupAp,
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
                            MaterializationFeasibility.AddIfFeasibleA(candidates, p, demand, def, stealthSurcharge, reservedFollowupAp,
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
                            MaterializationFeasibility.AddIfFeasibleA(candidates, p, demand, hd, stealthSurcharge, reservedFollowupAp,
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
                            MaterializationFeasibility.AddIfFeasibleA(candidates, p, demand, gd, stealthSurcharge, reservedFollowupAp,
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
                            MaterializationFeasibility.AddIfFeasibleA(candidates, p, demand, gd, stealthSurcharge, reservedFollowupAp,
                                axisBudget, eps, root, hand, player, ctx);
                        }
                    }
                }
            }
            return candidates;
        }

        // Phase B — every preflighted surplus plan (score-free; BestSurplus scores + ranks).
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
                    if (strategicClaim != null && !MaterializationDeliveryPolicy.CanDeliverDemandOperationally(direct, strategicClaim))
                        continue;
                    if (StrategicSpendability.ReservesOkAfterChain(root, ctx, direct, player))
                    {
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
                        if (strategicClaim != null && !MaterializationDeliveryPolicy.CanDeliverDemandOperationally(att, strategicClaim))
                            continue;
                        if (!StrategicSpendability.ReservesOkAfterChain(root, ctx, att, player)) continue;
                        // P1.4 — the evaluator owns the attach-step penalty (ChainStepPenalty in
                        // ResourceEfficiency); no extra subtraction here.
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
                                if (strategicClaim != null && !MaterializationDeliveryPolicy.CanDeliverDemandOperationally(genEq, strategicClaim))
                                    continue;
                                if (!StrategicSpendability.ReservesOkAfterChain(root, ctx, genEq, player))
                                    continue;

                                // P1.4 — the evaluator owns the generation + attach step penalties
                                // (ChainStepPenalty) AND the generation success-chance discount
                                // (Deployability). No re-application here.
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
                        if (genStrategicClaim != null && !MaterializationDeliveryPolicy.CanDeliverDemandOperationally(gen, genStrategicClaim))
                            continue;
                        if (gen.HandSlotsNeededAtPeak > 0 && !hand.HasFreeSlot) continue;
                        if (!StrategicSpendability.ReservesOkAfterChain(root, ctx, gen, player)) continue;
                        // P1.4 — evaluator owns the generation step penalty + success-chance discount.
                        candidates.Add(gen);
                    }
                }
            }

            return candidates;
        }

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
    }
}
