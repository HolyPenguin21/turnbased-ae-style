using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  PLACEMENT OPTION / SELECTOR  (Strategy V2 — Strategic Manager)
    // ===========================================================================================
    //  A legal deploy landing spot for a card, decoupled from the CardData so the SAME
    //  enumeration serves an existing hand card AND a not-yet-minted generated card. A solo
    //  (Recce / ScoutCapability) card only ever gets a shell-at-hex or a fresh army; a plain
    //  Unit/Hero also gets an existing suitable army / garrison with room at that hex. A shell is
    //  used only at its own hex; "create here" is always a separate alternative.
    // ===========================================================================================
    public readonly struct PlacementOption
    {
        public readonly HexCoord Hex;
        public readonly DeploymentKind Kind;
        public readonly ArmyData Army;   // null for NewArmy

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
                        if (PlacementRules.CanDepositIntoGarrison(a))
                            opts.Add(new PlacementOption(hex, DeploymentKind.Garrison, a));
                        continue;
                    }
                    if (!a.HasRoom || a.Members.Count == 0)
                        continue;
                    bool ok = AiArmyRoles.IsPlainReserveArmy(a)
                        || (isUnit && AiArmyRoles.IsHeroLedCombatArmy(a));
                    if (ok)
                        opts.Add(new PlacementOption(hex, DeploymentKind.ExistingArmy, a));
                }
            }
            return opts;
        }
    }

    // ===========================================================================================
    //  MATERIALIZATION RESERVATION  (Strategy V2 — Strategic Manager, Step 8B)
    // ===========================================================================================
    //  The single pass-local ownership view that stops two SELECTED chains from claiming the same
    //  limited generator use, and bounds how many generation Challenges a turn may attempt. Base
    //  cards / equipment cards need no explicit claim here: a selected chain is EXECUTED before
    //  the next candidate is enumerated, and the operational snapshot is refreshed, so a deployed
    //  card / a consumed equipment card is simply no longer in hand for the next enumeration.
    //  AxisBudgetLedger stays the owner of strategic AP boundaries; this is not a second budget.
    //
    //  The reservation also carries the RESIDUAL strategic demand set from Phase A into Phase B.
    //  Phase B remains late-turn preparation (it cannot retroactively execute a mission), but an
    //  executable residual strategic shortage must outrank generic surplus so unused AP prepares
    //  the requested capability for the next turn instead of playing an unrelated card.
    // ===========================================================================================
    public sealed class MaterializationReservation
    {
        public readonly HashSet<string> ClaimedGeneratorUses = new HashSet<string>();
        public readonly HashSet<string> TriedGeneratorCards = new HashSet<string>();
        public readonly List<AxisDemand> UnresolvedDemands = new List<AxisDemand>();
        public int GenerationAttemptsUsed;

        public bool CanGenerateMore => GenerationAttemptsUsed < AiConfigV2.maxGenerationActionsPerTurn;

        public void RecordGenerationAttempt(GenerationStep g, MaterializationResult r)
        {
            if (g == null)
                return;
            GenerationAttemptsUsed++;
            if (!string.IsNullOrEmpty(g.UseKey))
                ClaimedGeneratorUses.Add(g.UseKey);
            if (!string.IsNullOrEmpty(g.CardKey))
                TriedGeneratorCards.Add(g.CardKey);
            if (r != null && !string.IsNullOrEmpty(r.AttemptedGenerationUseKey))
                ClaimedGeneratorUses.Add(r.AttemptedGenerationUseKey);
        }

        public AxisDemand BestUnresolvedDemandFor(MaterializationPlan plan)
        {
            if (plan == null)
                return null;
            return UnresolvedDemands
                .Where(d => d != null && d.DesiredAmount > 0f && d.Capability == plan.FinalCapability
                    && (plan.ExpectedTraits & d.RequiredTraits) == d.RequiredTraits)
                .OrderByDescending(d => d.Value)
                .ThenBy(d => (int)d.RequestingAxis)
                .FirstOrDefault();
        }
    }

    // ===========================================================================================
    //  MATERIALIZATION CANDIDATE BUILDER  (Strategy V2 — Strategic Manager, Step 8B)
    // ===========================================================================================
    internal static class MaterializationCandidateBuilder
    {
        public static (MaterializationPlan plan, float followupAp)? BestForDemand(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand,
            AxisBudgetLedger ledger, ActorCommitments commitments, float reservedFollowupAp,
            MaterializationReservation reservation, CapabilityInventory inv, bool hasCompetingHeroDemand)
        {
            float eps = AiConfigV2.allocatorSliceEpsilon;
            float axisBudget = ledger.DiscreteAdmissionBudget(demand.RequestingAxis);
            bool soloOnly = demand.Capability == CapabilityKind.ScoutCapability;
            int stealthSurcharge = (demand.RequiredTraits & TraitPreference.Stealth) != 0
                ? AiConfigV2.scoutOptionalStealthAp : 0;

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
                if (def == null || def.isAviation || !MatchesCapabilityDef(def, demand.Capability))
                    continue;

                IReadOnlyList<string> baseAbilities = EffectiveAbilities(def, card.Equipment);

                if (AbilitiesSatisfyCapability(baseAbilities, def.cardType, demand.Capability)
                    && MeetsRequiredTraits(baseAbilities, demand.RequiredTraits))
                {
                    foreach (PlacementOption opt in PlacementSelector.BuildOptions(snap, player, def, commitments, soloOnly))
                    {
                        if (!CardPlayExecutor.Preflight(player, root, hand, ctx, opt.Bind(card), out _))
                            continue;
                        MaterializationPlan p = MakeExistingPlan(MaterializationChainKind.Direct, demand,
                            card, i, null, -1, opt, baseAbilities);
                        AddIfFeasibleA(candidates, p, demand, def, stealthSurcharge, reservedFollowupAp,
                            axisBudget, eps, root, hand, player);
                    }
                }

                if (card.Equipment == null)
                {
                    for (int j = 0; j < handList.Count; j++)
                    {
                        if (j == i)
                            continue;
                        CardData eq = handList[j];
                        CardDefinition eqDef = eq?.Definition;
                        if (eqDef == null || eqDef.cardType != CardType.Equipment || eqDef.equipment == null)
                            continue;
                        if (!EquipmentDefFitsHostDef(eqDef, def))
                            continue;
                        List<string> projected = EquipmentSystem.EffectiveAbilities(baseAbilities, eqDef.equipment);
                        if (!AbilitiesSatisfyCapability(projected, def.cardType, demand.Capability))
                            continue;
                        if (!MeetsRequiredTraits(projected, demand.RequiredTraits))
                            continue;

                        foreach (PlacementOption opt in PlacementSelector.BuildOptions(snap, player, def, commitments, soloOnly))
                        {
                            if (!CardPlayExecutor.Preflight(player, root, hand, ctx, opt.Bind(card), out _))
                                continue;
                            MaterializationPlan p = MakeExistingPlan(MaterializationChainKind.AttachDeploy, demand,
                                card, i, eq, j, opt, projected);
                            AddIfFeasibleA(candidates, p, demand, def, stealthSurcharge, reservedFollowupAp,
                                axisBudget, eps, root, hand, player);
                        }
                    }
                }
            }

            foreach (GenerationStep g in genSteps)
            {
                CardDefinition gd = g.CardDef;

                if (g.ProducesEquipment)
                {
                    if (gd.equipment == null)
                        continue;
                    for (int i = 0; i < handList.Count; i++)
                    {
                        CardData host = handList[i];
                        CardDefinition hd = host?.Definition;
                        if (hd == null || hd.isAviation || host.Equipment != null)
                            continue;
                        if (!MatchesCapabilityDef(hd, demand.Capability) || !EquipmentDefFitsHostDef(gd, hd))
                            continue;
                        IReadOnlyList<string> hostAbilities = EffectiveAbilities(hd, null);
                        List<string> projected = EquipmentSystem.EffectiveAbilities(hostAbilities, gd.equipment);
                        if (!AbilitiesSatisfyCapability(projected, hd.cardType, demand.Capability))
                            continue;
                        if (!MeetsRequiredTraits(projected, demand.RequiredTraits))
                            continue;

                        foreach (PlacementOption opt in PlacementSelector.BuildOptions(snap, player, hd, commitments, soloOnly))
                        {
                            MaterializationPlan p = MakeGeneratedPlan(MaterializationChainKind.GenerateAttachDeploy,
                                demand, g, baseInHand: host, baseIdx: i, generatedIsEquipment: true, opt: opt,
                                projected: projected);
                            AddIfFeasibleA(candidates, p, demand, hd, stealthSurcharge, reservedFollowupAp,
                                axisBudget, eps, root, hand, player);
                        }
                    }
                }
                else
                {
                    if ((gd.cardType != CardType.Unit && gd.cardType != CardType.Hero) || gd.isAviation)
                        continue;
                    if (!MatchesCapabilityDef(gd, demand.Capability))
                        continue;
                    IReadOnlyList<string> genAbilities = EffectiveAbilities(gd, null);
                    List<PlacementOption> genOpts = PlacementSelector.BuildOptions(snap, player, gd, commitments, soloOnly);
                    if (genOpts.Count == 0)
                        continue;

                    if (AbilitiesSatisfyCapability(genAbilities, gd.cardType, demand.Capability)
                        && MeetsRequiredTraits(genAbilities, demand.RequiredTraits))
                    {
                        foreach (PlacementOption opt in genOpts)
                        {
                            MaterializationPlan p = MakeGeneratedPlan(MaterializationChainKind.GenerateDeploy,
                                demand, g, baseInHand: null, baseIdx: -1, generatedIsEquipment: false, opt: opt,
                                projected: genAbilities);
                            AddIfFeasibleA(candidates, p, demand, gd, stealthSurcharge, reservedFollowupAp,
                                axisBudget, eps, root, hand, player);
                        }
                    }

                    for (int j = 0; j < handList.Count; j++)
                    {
                        CardData eq = handList[j];
                        CardDefinition eqDef = eq?.Definition;
                        if (eqDef == null || eqDef.cardType != CardType.Equipment || eqDef.equipment == null)
                            continue;
                        if (!EquipmentDefFitsHostDef(eqDef, gd))
                            continue;
                        List<string> projected = EquipmentSystem.EffectiveAbilities(genAbilities, eqDef.equipment);
                        if (!AbilitiesSatisfyCapability(projected, gd.cardType, demand.Capability))
                            continue;
                        if (!MeetsRequiredTraits(projected, demand.RequiredTraits))
                            continue;

                        foreach (PlacementOption opt in genOpts)
                        {
                            MaterializationPlan p = MakeGeneratedPlan(MaterializationChainKind.GenerateAttachDeploy,
                                demand, g, baseInHand: null, baseIdx: -1, generatedIsEquipment: false, opt: opt,
                                projected: projected, equipInHand: eq, equipIdx: j);
                            AddIfFeasibleA(candidates, p, demand, gd, stealthSurcharge, reservedFollowupAp,
                                axisBudget, eps, root, hand, player);
                        }
                    }
                }
            }

            if (candidates.Count == 0)
                return null;

            // Mobility is marginal against the cheapest useful feasible chain, not the slowest
            // body in the candidate set. An expensive Move1 generation chain must not manufacture
            // artificial "Move3 beats Move1" value when a cheap Move2 scout is the real alternative.
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
                c.plan.Score = ScorePlanA(c.plan, demand, c.proj, inv, referenceMoveMax, hasCompetingHeroDemand);

            var ranked = candidates
                .OrderByDescending(c => c.plan.Score)
                .ThenBy(c => c.plan.StableKey, System.StringComparer.Ordinal)
                .ToList();

            LogQualityChoice(demand, ranked);
            return (ranked[0].plan, ranked[0].followupAp);
        }

        private static void LogQualityChoice(AxisDemand demand,
            List<(MaterializationPlan plan, float followupAp, TraitPreference proj)> ranked)
        {
            if (demand.Capability != CapabilityKind.ScoutCapability || ranked.Count == 0)
                return;
            MaterializationPlan win = ranked[0].plan;

            (MaterializationPlan plan, float followupAp, TraitPreference proj)? runner = null;
            foreach (var c in ranked.Skip(1))
            {
                bool differentBody = c.plan.Kind != win.Kind
                    || !ReferenceEquals(c.plan.BaseCardInHand, win.BaseCardInHand)
                    || c.plan.GeneratedBaseDef != win.GeneratedBaseDef;
                bool close = win.Score - c.plan.Score <= AiConfigV2.scoutQualityLogRunnerUpMargin;
                if (differentBody || close)
                {
                    runner = c;
                    break;
                }
            }
            if (runner == null)
                return;

            string Name(MaterializationPlan p) =>
                (p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef)?.displayName ?? "?";
            string bdW = win.QualityBreakdown != null ? win.QualityBreakdown.ToCompact() : "-";
            string bdR = runner.Value.plan.QualityBreakdown != null
                ? runner.Value.plan.QualityBreakdown.ToCompact() : "-";
            AiDebugLog.Write($"[AI][V2]   strat.A quality {demand.Capability} — "
                + $"{Name(win)} score {win.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} "
                + $"[{bdW}] > {Name(runner.Value.plan)} "
                + $"{runner.Value.plan.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} "
                + $"[{bdR}]");
        }

        public static (MaterializationPlan plan, float utility)? BestSurplus(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            CapabilityInventory inv, ActorCommitments commitments, MaterializationReservation reservation)
        {
            List<CardData> handList = hand.Hand.ToList();
            var candidates = new List<MaterializationPlan>();

            for (int i = 0; i < handList.Count; i++)
            {
                CardData card = handList[i];
                CardDefinition def = card?.Definition;
                if (def == null || def.isAviation)
                    continue;
                bool recce = AbilityParams.AbilitiesHaveAnyRecce(def.grantedAbilities);
                bool hero = def.cardType == CardType.Hero;
                if (!recce && def.cardType != CardType.Unit && !hero)
                    continue;

                CapabilityKind cap = recce ? CapabilityKind.ScoutCapability
                    : hero ? CapabilityKind.Hero : CapabilityKind.FieldCombatPower;
                bool soloOnly = cap == CapabilityKind.ScoutCapability;
                IReadOnlyList<string> baseAbilities = EffectiveAbilities(def, card.Equipment);

                foreach (PlacementOption opt in PlacementSelector.BuildOptions(snap, player, def, commitments, soloOnly))
                {
                    if (!CardPlayExecutor.Preflight(player, root, hand, ctx, opt.Bind(card), out _))
                        continue;
                    MaterializationPlan direct = MakeExistingPlan(MaterializationChainKind.Direct, null,
                        card, i, null, -1, opt, baseAbilities);
                    direct.FinalCapability = cap;
                    if (StrategicManager.ReservesOkAfterChain(root, direct))
                    {
                        direct.Score = SurplusUtility(direct, inv, recce, hero, hand, baseAbilities);
                        candidates.Add(direct);
                    }

                    if (!AiConfigV2.surplusAllowAttach || card.Equipment != null)
                        continue;
                    for (int j = 0; j < handList.Count; j++)
                    {
                        if (j == i)
                            continue;
                        CardData eq = handList[j];
                        CardDefinition eqDef = eq?.Definition;
                        if (eqDef == null || eqDef.cardType != CardType.Equipment || eqDef.equipment == null)
                            continue;
                        if (!EquipmentDefFitsHostDef(eqDef, def))
                            continue;
                        List<string> projected = EquipmentSystem.EffectiveAbilities(baseAbilities, eqDef.equipment);
                        if (!AbilitiesSatisfyCapability(projected, def.cardType, cap))
                            continue;
                        bool addsStealth = !AbilityParams.AbilitiesHaveAnyStealth(baseAbilities)
                            && AbilityParams.AbilitiesHaveAnyStealth(projected);
                        if (!addsStealth)
                            continue;
                        MaterializationPlan att = MakeExistingPlan(MaterializationChainKind.AttachDeploy, null,
                            card, i, eq, j, opt, projected);
                        att.FinalCapability = cap;
                        if (!StrategicManager.ReservesOkAfterChain(root, att))
                            continue;
                        att.Score = SurplusUtility(att, inv, recce, hero, hand, projected)
                            + AiConfigV2.surplusAttachTraitBonus
                            - AiConfigV2.stratChainAttachStepPenalty;
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
                    if (g.ProducesEquipment || gd.isAviation)
                        continue;
                    if (gd.cardType != CardType.Unit && gd.cardType != CardType.Hero)
                        continue;
                    bool recce = AbilityParams.AbilitiesHaveAnyRecce(gd.grantedAbilities);
                    bool hero = gd.cardType == CardType.Hero;
                    CapabilityKind cap = recce ? CapabilityKind.ScoutCapability
                        : hero ? CapabilityKind.Hero : CapabilityKind.FieldCombatPower;
                    bool soloOnly = cap == CapabilityKind.ScoutCapability;
                    IReadOnlyList<string> genAbilities = EffectiveAbilities(gd, null);

                    foreach (PlacementOption opt in PlacementSelector.BuildOptions(snap, player, gd, commitments, soloOnly))
                    {
                        MaterializationPlan gen = MakeGeneratedPlan(MaterializationChainKind.GenerateDeploy,
                            null, g, baseInHand: null, baseIdx: -1, generatedIsEquipment: false, opt: opt,
                            projected: genAbilities);
                        gen.FinalCapability = cap;
                        if (gen.HandSlotsNeededAtPeak > 0 && !hand.HasFreeSlot)
                            continue;
                        if (!StrategicManager.ReservesOkAfterChain(root, gen))
                            continue;
                        float util = (SurplusUtility(gen, inv, recce, hero, hand, genAbilities)
                                      - AiConfigV2.stratChainGenerationStepPenalty)
                                     * Mathf.Lerp(AiConfigV2.stratChainGenerationChanceFloor, 1f,
                                         Mathf.Clamp01(g.SuccessChance));
                        gen.Score = util;
                        candidates.Add(gen);
                    }
                }
            }

            if (candidates.Count == 0)
                return null;

            MaterializationPlan bestPlan = candidates
                .OrderByDescending(p => reservation?.BestUnresolvedDemandFor(p) != null ? 1 : 0)
                .ThenByDescending(p => reservation?.BestUnresolvedDemandFor(p)?.Value ?? 0f)
                .ThenByDescending(p => p.Score)
                .ThenBy(p => p.StableKey, System.StringComparer.Ordinal)
                .First();
            return (bestPlan, bestPlan.Score);
        }

        private static MaterializationPlan MakeExistingPlan(MaterializationChainKind kind, AxisDemand demand,
            CardData baseCard, int baseIdx, CardData equip, int equipIdx, PlacementOption opt,
            IReadOnlyList<string> projected)
        {
            var p = new MaterializationPlan
            {
                Kind = kind,
                OwnerAxis = demand?.RequestingAxis,
                FinalCapability = demand?.Capability ?? CapabilityKind.FieldCombatPower,
                ExpectedTraits = TraitsOf(projected),
                BaseCardInHand = baseCard,
                EquipmentInHand = kind == MaterializationChainKind.AttachDeploy ? equip : null,
                Deploy = opt,
                ProjectedAbilities = projected,
            };
            FillCostsAndKey(p, baseCard.Definition, baseCard, equip, baseIdx, equipIdx, -1);
            return p;
        }

        private static MaterializationPlan MakeGeneratedPlan(MaterializationChainKind kind, AxisDemand demand,
            GenerationStep g, CardData baseInHand, int baseIdx, bool generatedIsEquipment, PlacementOption opt,
            IReadOnlyList<string> projected, CardData equipInHand = null, int equipIdx = -1)
        {
            var p = new MaterializationPlan
            {
                Kind = kind,
                OwnerAxis = demand?.RequestingAxis,
                FinalCapability = demand?.Capability ?? CapabilityKind.FieldCombatPower,
                ExpectedTraits = TraitsOf(projected),
                Generation = g,
                Deploy = opt,
                ProjectedAbilities = projected,
            };

            CardDefinition baseDef;
            if (generatedIsEquipment)
            {
                p.BaseCardInHand = baseInHand;
                p.GeneratedEquipmentDef = g.CardDef;
                baseDef = baseInHand.Definition;
            }
            else
            {
                p.GeneratedBaseDef = g.CardDef;
                p.EquipmentInHand = kind == MaterializationChainKind.GenerateAttachDeploy ? equipInHand : null;
                baseDef = g.CardDef;
            }

            FillCostsAndKey(p, baseDef, p.BaseCardInHand, p.EquipmentInHand ?? equipInHand, baseIdx, equipIdx, 0);
            return p;
        }

        private static void FillCostsAndKey(MaterializationPlan p, CardDefinition baseDef, CardData baseInstance,
            CardData equipInstance, int baseIdx, int equipIdx, int genMark)
        {
            int human = 0, energy = 0, materials = 0, tech = 0;
            float ap = 0f;

            ap += p.Deploy.Kind == DeploymentKind.NewArmy ? ArmyActions.CreateArmyApCost : 0;
            if (p.GeneratedBaseDef != null)
            {
                ap += baseDef != null ? ArmyActions.EffectiveDeployApCost(baseDef) : 0;
            }
            else if (baseInstance != null)
            {
                ap += baseInstance.EffectivePlayApCost;
                Accumulate(baseInstance.EffectivePlayResourceCost, ref human, ref energy, ref materials, ref tech);
            }

            if (p.UsesEquipment)
            {
                if (p.GeneratedEquipmentDef != null)
                {
                    ap += p.GeneratedEquipmentDef.activationApCost;
                }
                else if (equipInstance != null)
                {
                    ap += equipInstance.EffectivePlayApCost;
                    Accumulate(equipInstance.EffectivePlayResourceCost, ref human, ref energy, ref materials, ref tech);
                }
            }

            if (p.Generation != null && p.Generation.CardDef?.resourceCost != null)
            {
                ResourceCost rc = p.Generation.CardDef.resourceCost;
                human += rc.human; energy += rc.energy; materials += rc.materials; tech += rc.tech;
            }

            p.ApCost = ap;
            p.ResCost = (human | energy | materials | tech) == 0
                ? null
                : new ResourceCost { human = human, energy = energy, materials = materials, tech = tech };
            p.HandSlotsNeededAtPeak = p.Generation != null ? 1 : 0;

            string baseKey = p.GeneratedBaseDef != null
                ? "gen:" + p.GeneratedBaseDef.displayName
                : (baseDef != null ? baseDef.displayName : "?") + ":" + baseIdx;
            string eqKey = p.GeneratedEquipmentDef != null
                ? "gen:" + p.GeneratedEquipmentDef.displayName
                : (p.EquipmentInHand?.Definition != null ? p.EquipmentInHand.Definition.displayName + ":" + equipIdx : "-");
            string genKey = p.Generation != null ? p.Generation.CardKey : "-";
            p.StableKey = $"{(int)p.Kind}|{(int)p.FinalCapability}|{baseKey}|{eqKey}|{genKey}|{p.Deploy.Key}";
        }

        private static void Accumulate(ResourceCost c, ref int h, ref int e, ref int m, ref int t)
        {
            if (c == null)
                return;
            h += c.human; e += c.energy; m += c.materials; t += c.tech;
        }

        private static void AddIfFeasibleA(
            List<(MaterializationPlan plan, float followupAp, TraitPreference proj)> sink,
            MaterializationPlan p, AxisDemand demand, CardDefinition baseDef, int stealthSurcharge,
            float reservedFollowupAp, float axisBudget, float eps, PlayerRoot root, AiHandData hand,
            PlayerSetupData player)
        {
            // Follow-up belongs to the projected END body. Equipment may change Move/activation AP
            // (or grant RapidReaction), so reserving baseDef.activationApCost can over/under-fund
            // the mission even when the materialization chain itself was correctly affordable.
            float activationAp = p != null
                ? CapabilityQualityEvaluator.ProjectedActivationApCost(p)
                : (baseDef != null ? baseDef.activationApCost : AiConfigV2.scoutNotionalActivationAp);
            float followupAp = activationAp + stealthSurcharge + demand.MinimumFollowupAp;

            float need = p.ApCost + reservedFollowupAp + followupAp;
            if (need > axisBudget + eps)
                return;
            if (root.ActionPoints - need - AiConfigV2.housekeepingApReserve < -eps)
                return;
            if (!ChainResourcesAffordable(root, player, p.ResCost))
                return;
            if (p.HandSlotsNeededAtPeak > 0 && !hand.HasFreeSlot)
                return;

            sink.Add((p, followupAp, p.ExpectedTraits));
        }

        private static bool ChainResourcesAffordable(PlayerRoot root, PlayerSetupData player, ResourceCost cost)
        {
            if (cost == null)
                return true;
            foreach (Game.Economy.ResourceType t in ResourceBundle.All)
                if (AiResourceReservation.Available(root, player, t) < cost.Get(t))
                    return false;
            return true;
        }

        private static float ScorePlanA(MaterializationPlan p, AxisDemand demand, TraitPreference projected,
            CapabilityInventory inv, int referenceMoveMax, bool hasCompetingHeroDemand)
        {
            float fit = TargetFit(p.Deploy.Hex, demand.TargetHex);
            float resSum = ResourceCostSum(p.ResCost);
            float costFactor = 1f + AiConfigV2.stratCardApCostWeight * p.ApCost
                                  + AiConfigV2.stratChainResCostWeight * resSum;

            // Scout Preferred-Stealth is already contextual in ScoutCapabilityQuality. Keeping the
            // old unconditional +0.35 here would double-count it and make a safe dark-map Scout
            // choice hinge on the tag rather than mobility/information value.
            float traitBonus = demand.Capability != CapabilityKind.ScoutCapability
                               && (demand.PreferredTraits & TraitPreference.Stealth) != 0
                               && (projected & TraitPreference.Stealth) != 0
                ? AiConfigV2.stratTraitMatchBonus : 0f;

            float score = (1f + traitBonus) * (0.5f + 0.5f * fit) / Mathf.Max(0.0001f, costFactor);
            score += PlacementBonus(p.Deploy.Kind);
            if (p.Generation != null)
                score *= Mathf.Lerp(AiConfigV2.stratChainGenerationChanceFloor, 1f,
                    Mathf.Clamp01(p.Generation.SuccessChance));

            // Apply quality while the score is still a positive benefit measure. Multiplying after
            // subtracting penalties can invert ranking: x1.6 makes a negative score more negative.
            float qm = CapabilityQualityEvaluator.QualityMultiplier(
                p, demand, inv, referenceMoveMax, hasCompetingHeroDemand,
                out MaterializationQualityBreakdown qbd);
            p.QualityBreakdown = qbd;
            score *= qm;

            score -= ChainStepPenalty(p.Kind);
            score -= ScarcityOpportunityCost(p, demand, inv);
            return score;
        }

        private static float ScarcityOpportunityCost(MaterializationPlan p, AxisDemand demand, CapabilityInventory inv)
        {
            float cost = 0f;

            // A stealth Scout used as a Scout preserves that capability on the map; do not treat it
            // as "consuming a scarce stealth item" merely because stealth was Preferred rather than
            // Required. This generic preservation penalty is for spending stealth outside Scout work.
            if (demand.Capability != CapabilityKind.ScoutCapability
                && (demand.RequiredTraits & TraitPreference.Stealth) == 0)
            {
                bool consumesExistingStealth =
                    (p.BaseCardInHand != null && CardCarriesStealth(p.BaseCardInHand))
                    || (p.EquipmentInHand?.Definition?.equipment != null
                        && GrantAddsStealth(p.EquipmentInHand.Definition.equipment));
                if (consumesExistingStealth
                    && !(inv != null && inv.StealthScouts > AiConfigV2.stratChainStealthScarceAt))
                    cost += AiConfigV2.stratChainScarcityPenalty;
            }

            if (demand.Capability != CapabilityKind.Hero && demand.Capability != CapabilityKind.ScoutCapability)
            {
                CardDefinition baseDef = p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef;
                if (baseDef != null && baseDef.cardType == CardType.Hero
                    && inv != null && inv.AvailableHeroes <= AiConfigV2.stratChainHeroScarceAt)
                    cost += AiConfigV2.stratChainHeroScarcityPenalty;
            }

            return cost;
        }

        private static float ChainStepPenalty(MaterializationChainKind k)
        {
            switch (k)
            {
                case MaterializationChainKind.AttachDeploy:
                    return AiConfigV2.stratChainAttachStepPenalty;
                case MaterializationChainKind.GenerateDeploy:
                    return AiConfigV2.stratChainGenerationStepPenalty;
                case MaterializationChainKind.GenerateAttachDeploy:
                    return AiConfigV2.stratChainAttachStepPenalty + AiConfigV2.stratChainGenerationStepPenalty;
                default:
                    return 0f;
            }
        }

        private static float ResourceCostSum(ResourceCost c) => c == null
            ? 0f
            : c.human + c.energy + c.materials + c.tech;

        private static float SurplusUtility(MaterializationPlan p, CapabilityInventory inv, bool recce, bool hero,
            AiHandData hand, IReadOnlyList<string> projected)
        {
            float scarcity = SurplusScarcity(inv, recce, hero);
            float versatility = hero ? AiConfigV2.surplusHeroVersatility : AiConfigV2.surplusUnitVersatility;
            float traits = projected != null && AbilityParams.AbilitiesHaveAnyStealth(projected)
                ? AiConfigV2.stratTraitMatchBonus : 0f;
            float handPressure = hand.HasFreeSlot ? 0f : AiConfigV2.surplusHandPressureBonus;
            float oversupply = recce
                && inv != null && inv.ReadyScouts + inv.ReserveScouts >= AiConfigV2.surplusScoutOversupplyAt
                ? AiConfigV2.surplusOversupplyPenalty : 0f;
            float resSum = ResourceCostSum(p.ResCost);

            return scarcity + versatility + traits + handPressure
                - AiConfigV2.surplusApCostWeight * p.ApCost
                - AiConfigV2.surplusResourceCostWeight * resSum
                - oversupply
                + PlacementBonus(p.Deploy.Kind);
        }

        private static float SurplusScarcity(CapabilityInventory inv, bool recce, bool hero)
        {
            if (inv == null)
                return AiConfigV2.surplusScarcityLow;
            if (recce)
            {
                if (inv.TotalScouts <= 0)
                    return AiConfigV2.surplusScarcityHigh;
                if (inv.ReadyScouts + inv.ReserveScouts <= 1)
                    return AiConfigV2.surplusScarcityMed;
                return AiConfigV2.surplusScarcityLow;
            }
            if (hero)
                return inv.AvailableHeroes <= 0 ? AiConfigV2.surplusScarcityMed : AiConfigV2.surplusScarcityLow;
            return AiConfigV2.surplusScarcityLow;
        }

        private static IReadOnlyList<string> EffectiveAbilities(CardDefinition def, CardDefinition attachedEquipment)
        {
            var baseList = def?.grantedAbilities != null
                ? new List<string>(def.grantedAbilities)
                : new List<string>();
            if (attachedEquipment?.equipment == null)
                return baseList;
            return EquipmentSystem.EffectiveAbilities(baseList, attachedEquipment.equipment);
        }

        private static bool MatchesCapabilityDef(CardDefinition d, CapabilityKind kind)
        {
            if (d == null || d.isAviation)
                return false;
            bool recce = AbilityParams.AbilitiesHaveAnyRecce(d.grantedAbilities);
            switch (kind)
            {
                case CapabilityKind.ScoutCapability:
                    return recce;
                case CapabilityKind.Hero:
                    return d.cardType == CardType.Hero && !recce;
                case CapabilityKind.FieldCombatPower:
                case CapabilityKind.GarrisonCombatPower:
                    return !recce && (d.cardType == CardType.Unit || d.cardType == CardType.Hero);
                default:
                    return false;
            }
        }

        private static bool AbilitiesSatisfyCapability(IReadOnlyList<string> abilities, CardType type, CapabilityKind kind)
        {
            bool recce = AbilityParams.AbilitiesHaveAnyRecce(abilities);
            switch (kind)
            {
                case CapabilityKind.ScoutCapability:
                    return recce;
                case CapabilityKind.Hero:
                    return type == CardType.Hero;
                case CapabilityKind.FieldCombatPower:
                case CapabilityKind.GarrisonCombatPower:
                    return type == CardType.Unit || type == CardType.Hero;
                default:
                    return false;
            }
        }

        private static bool MeetsRequiredTraits(IReadOnlyList<string> abilities, TraitPreference required)
        {
            if (required == TraitPreference.None)
                return true;
            if ((required & TraitPreference.Stealth) != 0 && !AbilityParams.AbilitiesHaveAnyStealth(abilities))
                return false;
            if ((required & (TraitPreference.AntiArmour | TraitPreference.Ranged | TraitPreference.Melee)) != 0)
                return false;
            return true;
        }

        private static TraitPreference TraitsOf(IReadOnlyList<string> abilities)
        {
            TraitPreference t = TraitPreference.None;
            if (AbilityParams.AbilitiesHaveAnyStealth(abilities))
                t |= TraitPreference.Stealth;
            return t;
        }

        private static bool CardCarriesStealth(CardData c) =>
            c?.Definition != null && AbilityParams.AbilitiesHaveAnyStealth(EffectiveAbilities(c.Definition, c.Equipment));

        private static bool GrantAddsStealth(EquipmentGrant grant) =>
            grant?.addAbilities != null && grant.addAbilities.Any(a => AbilityParams.TryGetStealthLevel(a, out _));

        private static bool EquipmentDefFitsHostDef(CardDefinition eq, CardDefinition host)
        {
            if (eq == null || eq.cardType != CardType.Equipment || eq.equipment == null)
                return false;
            if (host == null || (host.cardType != CardType.Unit && host.cardType != CardType.Hero))
                return false;
            EquipmentHostKind kind = host.cardType == CardType.Hero ? EquipmentHostKind.Hero : EquipmentHostKind.Unit;
            EquipmentGrant grant = eq.equipment;
            if (grant.hostKinds == null || !grant.hostKinds.Contains(kind))
                return false;
            if (grant.hostTypeTags != null && grant.hostTypeTags.Count > 0)
            {
                if (host.unitTypeTags == null
                    || !grant.hostTypeTags.Any(need => host.unitTypeTags.Contains(need)))
                    return false;
            }
            return true;
        }

        private static float PlacementBonus(DeploymentKind k)
        {
            switch (k)
            {
                case DeploymentKind.Garrison:      return AiConfigV2.stratPlacementGarrisonBonus;
                case DeploymentKind.ExistingArmy:  return AiConfigV2.stratPlacementExistingArmyBonus;
                case DeploymentKind.ReusableShell: return AiConfigV2.stratPlacementReusableShellBonus;
                default:                           return 0f;
            }
        }

        private static float TargetFit(HexCoord deployHex, HexCoord? target)
        {
            if (!target.HasValue)
                return 0.5f;
            int d = HexGridMath.Distance(deployHex, target.Value);
            return Mathf.Clamp01(1f - d / Mathf.Max(1f, (float)AiConfigV2.stratTargetFitRange));
        }
    }
}
