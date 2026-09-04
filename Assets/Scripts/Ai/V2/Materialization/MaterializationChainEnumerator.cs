using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ARCH-02 §9/§10 / DoD "Enumeration отделена от feasibility ... от scoring" — the chain
    // enumerator OWNS ONLY THE SET OF LEGAL CHAIN SHAPES (Direct / Attach->Deploy /
    // Generate->Deploy / Generate->Attach->Deploy). It consults MaterializationChainMatching for
    // capability/trait relevance, PlacementSelector for placement options and
    // MaterializationPlanFactory for construction. It does NOT preflight a placement, does NOT
    // apply the Phase-A AP/resource/entitlement gate, does NOT apply the Phase-B reserves /
    // strategic-claim gate and does NOT score. The caller composes the seam:
    //   enumerate raw  ->  MaterializationFeasibility.Filter*  ->  StrategicCardEvaluator  ->  PortfolioSolver
    internal static class MaterializationChainEnumerator
    {
        // Phase A — every structurally-applicable chain shape for one capability demand. RAW: no
        // preflight, no feasibility gate, no score. MaterializationFeasibility.FilterForDemand turns
        // this into the admitted (plan, followupAp, proj) set.
        internal static List<MaterializationPlan> EnumerateForDemand(
            WorldSnapshot snap, PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            AxisDemand demand, ActorCommitments commitments, MaterializationReservation reservation,
            System.Collections.Generic.ISet<CardData> excludeCards = null,
            System.Collections.Generic.ISet<string> excludeGenKeys = null)
        {
            bool soloOnly = demand.Capability == CapabilityKind.ScoutCapability;
            bool Excluded(CardData c) => c != null && excludeCards != null && excludeCards.Contains(c);
            bool ExcludedGen(GenerationStep g) => g != null && excludeGenKeys != null
                && !string.IsNullOrEmpty(g.CardKey) && excludeGenKeys.Contains(g.CardKey);

            List<CardData> handList = hand.Hand.ToList();
            List<GenerationStep> genSteps = reservation != null && reservation.CanGenerateMore
                ? GenerationSource.Enumerate(player, root, ctx, hand,
                    reservation.ClaimedGeneratorUses, reservation.TriedGeneratorCards)
                : new List<GenerationStep>();

            var candidates = new List<MaterializationPlan>();

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
                        candidates.Add(MaterializationPlanFactory.MakeExistingPlan(MaterializationChainKind.Direct, demand,
                            card, i, null, -1, opt, baseAbilities));
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
                            candidates.Add(MaterializationPlanFactory.MakeExistingPlan(MaterializationChainKind.AttachDeploy, demand,
                                card, i, eq, j, opt, projected));
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
                            candidates.Add(MaterializationPlanFactory.MakeGeneratedPlan(MaterializationChainKind.GenerateAttachDeploy,
                                demand, g, baseInHand: host, baseIdx: i, generatedIsEquipment: true, opt: opt,
                                projected: projected));
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
                            candidates.Add(MaterializationPlanFactory.MakeGeneratedPlan(MaterializationChainKind.GenerateDeploy,
                                demand, g, baseInHand: null, baseIdx: -1, generatedIsEquipment: false, opt: opt,
                                projected: genAbilities));
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
                            candidates.Add(MaterializationPlanFactory.MakeGeneratedPlan(MaterializationChainKind.GenerateAttachDeploy,
                                demand, g, baseInHand: null, baseIdx: -1, generatedIsEquipment: false, opt: opt,
                                projected: projected, equipInHand: eq, equipIdx: j));
                    }
                }
            }
            return candidates;
        }

        // Phase B — every structurally-applicable surplus chain shape. RAW: no preflight, no
        // reserves gate, no strategic-claim gate, no score. FinalCapability is set here because it
        // is a property of the shape. MaterializationFeasibility.FilterSurplus admits the set.
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

                foreach (PlacementOption opt in PlacementSelector.BuildOptions(snap, player, def, commitments, soloOnly))
                {
                    MaterializationPlan direct = MaterializationPlanFactory.MakeExistingPlan(MaterializationChainKind.Direct, null,
                        card, i, null, -1, opt, baseAbilities);
                    direct.FinalCapability = cap;
                    candidates.Add(direct);

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

                            foreach (PlacementOption opt in PlacementSelector.BuildOptions(snap, player, hd, commitments, soloOnly))
                            {
                                MaterializationPlan genEq = MaterializationPlanFactory.MakeGeneratedPlan(
                                    MaterializationChainKind.GenerateAttachDeploy, null, g,
                                    baseInHand: host, baseIdx: i, generatedIsEquipment: true,
                                    opt: opt, projected: projected);
                                genEq.FinalCapability = cap;
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

                    foreach (PlacementOption opt in PlacementSelector.BuildOptions(snap, player, gd, commitments, genSoloOnly))
                    {
                        MaterializationPlan gen = MaterializationPlanFactory.MakeGeneratedPlan(MaterializationChainKind.GenerateDeploy,
                            null, g, baseInHand: null, baseIdx: -1, generatedIsEquipment: false, opt: opt,
                            projected: genAbilities);
                        gen.FinalCapability = genCap;
                        candidates.Add(gen);
                    }
                }
            }

            return candidates;
        }
    }
}
